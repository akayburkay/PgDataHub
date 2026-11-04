using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Metadata;
using Swashbuckle.AspNetCore.Swagger;

namespace PgDataHub.Features;

[ApiController, AllowAnonymous]
public sealed class OpenApiController(ISwaggerProvider swagger, CollectionAccessService access,
    PgDataHubConfig config, AdminSessionService sessions, MetadataService metadata, AuthService auth) : ControllerBase
{
    [HttpGet("/api")]
    [HttpGet("/openapi.json")]
    [HttpGet("/swagger/v1/swagger.json")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        OgcSupport.ValidateFormat(Request, alternativeMediaType: "application/vnd.oai.openapi+json");
        var principal = User;
        if (config.Auth.Enabled && User.Identity?.IsAuthenticated != true)
        {
            if (!sessions.Validate(HttpContext, requireCsrf: false)) return Unauthorized();
            principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaims.Role, config.Auth.AdminRole)],
                "AdminSession", ClaimTypes.Name, AuthClaims.Role));
        }
        Response.Headers.CacheControl = "private, no-store";
        var document = JsonNode.Parse(await swagger.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0, ct))!.AsObject();
        document["servers"] = Node(new[] { new { url = OgcSupport.BaseUrl(Request) } });
        document["info"] = Node(new { title = "PgDataHub", version = "1.0", description = "OGC API Features, authorized collections and spatial analysis. Row policies apply to all collection queries." });
        var components = document["components"]!.AsObject();
        var schemas = components["schemas"]!.AsObject();
        var paths = document["paths"]!.AsObject();
        components["securitySchemes"] = Node(new {
            ApiKey = new { type = "apiKey", name = config.Auth.ApiKey.Header, @in = "header" },
            Bearer = new { type = "http", scheme = "bearer", bearerFormat = "JWT" } });
        if (config.Auth.Enabled) document["security"] = Node(new object[] { new { ApiKey = Array.Empty<string>() }, new { Bearer = Array.Empty<string>() } });

        foreach (var collection in await access.ListAsync(principal, ct))
        {
            var id = $"{collection.Schema}.{collection.Name}";
            var path = "/collections/" + Uri.EscapeDataString(id);
            var key = "Collection_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id));
            var properties = new JsonObject();
            foreach (var column in collection.Columns.Where(c => c.Name != collection.GeometryColumn))
                properties[column.Name] = ColumnSchema(column);
            schemas[key + "Properties"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            schemas[key + "Feature"] = Node(new {
                type = "object", required = new[] { "type", "geometry", "properties" },
                properties = new Dictionary<string, object> {
                    ["type"] = new { type = "string", @enum = new[] { "Feature" } },
                    ["id"] = new { oneOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    ["geometry"] = new { type = "object", nullable = true, additionalProperties = true },
                    ["properties"] = new Dictionary<string, string> { ["$ref"] = "#/components/schemas/" + key + "Properties" },
                    ["links"] = new { type = "array", items = new { type = "object", additionalProperties = true } } } });
            schemas[key + "Page"] = Node(new {
                type = "object", required = new[] { "type", "features", "numberReturned", "links" },
                properties = new Dictionary<string, object> {
                    ["type"] = new { type = "string", @enum = new[] { "FeatureCollection" } },
                    ["features"] = new { type = "array", items = new Dictionary<string, string> { ["$ref"] = "#/components/schemas/" + key + "Feature" } },
                    ["numberReturned"] = new { type = "integer", minimum = 0 },
                    ["numberMatched"] = new { type = "integer", minimum = 0 },
                    ["links"] = new { type = "array", items = new { type = "object", additionalProperties = true } } } });
            var parameters = Parameters(collection, config);
            paths[path + "/items"] = new JsonObject { ["get"] = Operation("Items " + id, parameters, key + "Page") };
            paths[path] = new JsonObject { ["get"] = Operation("Collection " + id, new JsonArray(), null) };
            paths[path + "/queryables"] = new JsonObject { ["get"] = Operation("Queryables " + id, new JsonArray(), null) };
            if (collection.PrimaryKey is not null)
                paths[path + "/items/{id}"] = new JsonObject { ["get"] = Operation("Feature " + id,
                    Node(new object[] { new { name = "id", @in = "path", required = true, schema = new { type = "string" } },
                        new { name = "crs", @in = "query", required = false, schema = new { type = "string" } },
                        new { name = "f", @in = "query", required = false, schema = new { type = "string", @enum = new[] { "json", "geojson" } } } })!.AsArray(), key + "Feature") };
            if (collection.IsSpatial)
            {
                var analysisProperties = properties.DeepClone().AsObject();
                analysisProperties["analysisValue"] = Node(new { oneOf = new object[] { new { type = "number" }, new { type = "boolean" } }, nullable = true });
                schemas[key + "AnalysisProperties"] = new JsonObject { ["type"] = "object", ["properties"] = analysisProperties, ["additionalProperties"] = false };
                var analysisFeature = schemas[key + "Feature"]!.DeepClone().AsObject();
                analysisFeature["properties"]!["properties"]!["$ref"] = "#/components/schemas/" + key + "AnalysisProperties";
                schemas[key + "AnalysisFeature"] = analysisFeature;
                var analysisPage = schemas[key + "Page"]!.DeepClone().AsObject();
                analysisPage["required"] = Node(new[] { "type", "features", "numberReturned" });
                analysisPage["properties"]!["features"]!["items"]!["$ref"] = "#/components/schemas/" + key + "AnalysisFeature";
                schemas[key + "AnalysisPage"] = analysisPage;
                foreach (var operation in SpatialAnalysis.Operations)
                {
                    var definition = Operation(operation.Name + " " + id, parameters.DeepClone().AsArray(), key + "AnalysisPage");
                    definition["description"] = operation.Description;
                    definition["requestBody"] = Node(new { required = true, content = new Dictionary<string, object> {
                        ["application/json"] = new { schema = new Dictionary<string, string> { ["$ref"] = "#/components/schemas/SpatialAnalysisRequest" } } } });
                    paths[path + "/analysis/" + operation.Name] = new JsonObject { ["post"] = definition };
                }
            }
        }
        foreach (var function in await metadata.GetFunctionsAsync(ct))
        {
            if (config.Auth.Enabled && !await auth.CanAccessFunctionAsync(principal.RoleNames(), function.Schema, function.Name, ct)) continue;
            var path = "/functions/" + function.Oid;
            paths[path] = new JsonObject { ["get"] = Operation("Metadata " + function.Signature, new JsonArray(), null) };
            if (function.IsVolatile && (!config.Auth.Enabled ||
                !await auth.CanExecuteVolatileFunctionAsync(principal.RoleNames(), function.Schema, function.Name, ct))) continue;
            var parameters = new JsonArray();
            foreach (var parameter in function.Parameters)
                parameters.Add(Node(new { name = parameter.Name, @in = "query", required = true,
                    schema = ColumnSchema(new() { Name = parameter.Name, Type = parameter.Type }) }));
            var execution = Operation("Execute " + function.Signature, parameters, null);
            execution["tags"] = Node(new[] { "Functions" });
            paths[path + "/items"] = new JsonObject { [function.IsVolatile ? "post" : "get"] = execution };
        }
        return new JsonResult(document) { ContentType = "application/vnd.oai.openapi+json;version=3.0" };
    }

    private static JsonObject Operation(string title, JsonArray parameters, string? schema)
    {
        var media = schema is null ? "application/json" : "application/geo+json";
        return new JsonObject {
            ["summary"] = title, ["tags"] = Node(new[] { "Collections" }), ["parameters"] = parameters,
            ["responses"] = Node(new Dictionary<string, object> {
                ["200"] = new { description = "Successful response", content = new Dictionary<string, object> {
                    [media] = new { schema = schema is null ? new Dictionary<string, string> { ["type"] = "object" } :
                        new Dictionary<string, string> { ["$ref"] = "#/components/schemas/" + schema } } } },
                ["400"] = new { description = "Invalid query or analysis parameters" },
                ["401"] = new { description = "Authentication required" },
                ["403"] = new { description = "Access denied" },
                ["404"] = new { description = "Collection or authorized feature not found" },
                ["406"] = new { description = "Unsupported representation" },
                ["429"] = new { description = "Request limit exceeded" } }) };
    }

    private static JsonArray Parameters(CollectionMetadata collection, PgDataHubConfig config)
    {
        var parameters = new JsonArray();
        void Add(string name, object schema, string description) => parameters.Add(Node(new { name, @in = "query", required = false, schema, description, style = "form", explode = false }));
        Add("limit", new { type = "integer", minimum = 0, maximum = config.Feature.MaxLimit, @default = config.Feature.DefaultLimit }, "Page size; next links preserve all filters.");
        Add("offset", new { type = "integer", minimum = 0 }, "Rows to skip after authorization and filtering.");
        Add("f", new { type = "string", @enum = new[] { "json", "geojson" } }, "Response representation.");
        Add("select", new { type = "string" }, "Comma-separated visible property names.");
        Add("orderby", new { type = "string" }, "Comma-separated properties; prefix - for descending.");
        Add("count", new { type = "boolean", @default = false }, "Include authorized numberMatched; may scan all matching rows.");
        Add("distinct", new { type = "boolean", @default = false }, "Deduplicate projected values.");
        Add("datetime", new { type = "string" }, "RFC3339 instant or start/end interval; first visible date/timestamp property is used. Missing temporal values match.");
        if (collection.IsSpatial)
        {
            Add("bbox", new { type = "array", items = new { type = "number" }, oneOf = new[] {
                new { minItems = 4, maxItems = 4 }, new { minItems = 6, maxItems = 6 } } }, "4/6-value bounding box; CRS84/CRS84h by default. Antimeridian wrapping is supported.");
            Add("bbox-crs", new { type = "string" }, "Bounding box CRS URI or EPSG code.");
            Add("crs", new { type = "string", @default = OgcSupport.CrsUri(4326, OgcSupport.HasHeight(collection)) }, "Output CRS; defaults to longitude/latitude WGS84.");
        }
        foreach (var column in collection.Columns.Where(c => c.Name != collection.GeometryColumn && !ReservedQueryKeys.Names.Contains(c.Name)))
            Add(column.Name, ColumnSchema(column), "Property filter; comparison operators and value lists are supported.");
        return parameters;
    }

    internal static JsonObject ColumnSchema(ColumnMetadata column)
    {
        var type = column.Type.ToLowerInvariant();
        if (type is "json" or "jsonb") return new JsonObject();
        if (type.EndsWith("[]", StringComparison.Ordinal)) return new JsonObject { ["type"] = "array",
            ["items"] = ColumnSchema(new() { Type = type[..^2] }), ["nullable"] = column.Nullable };
        var schema = new JsonObject { ["type"] = type is "smallint" or "integer" or "bigint" or "smallserial" or "serial" or "bigserial" ? "integer" : type == "boolean" ? "boolean" :
            type.StartsWith("numeric") || type.StartsWith("decimal") || type is "real" or "double precision" ? "number" : "string",
            ["nullable"] = column.Nullable };
        if (type.Contains("timestamp")) schema["format"] = "date-time";
        else if (type == "date") schema["format"] = "date";
        else if (type == "uuid") schema["format"] = "uuid";
        return schema;
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value);
}
