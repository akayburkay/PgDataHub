using Microsoft.AspNetCore.Mvc;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Metadata;
using System.Diagnostics;

namespace PgDataHub.Features;

[ApiController]
[Route("collections")]
public sealed class FeatureController : ControllerBase
{
    private readonly FeatureService _featureService;
    private readonly FeatureQueryBuilder _queryBuilder;
    private readonly AuthService _auth;
    private readonly PgDataHubConfig _config;
    private readonly CollectionAccessService _access;

    public FeatureController(
        FeatureService featureService,
        FeatureQueryBuilder queryBuilder,
        AuthService auth,
        PgDataHubConfig config,
        CollectionAccessService access)
    {
        _featureService = featureService;
        _queryBuilder = queryBuilder;
        _auth = auth;
        _config = config;
        _access = access;
    }

    /// <summary>
    /// Enforce collection access for the caller and return a column-narrowed
    /// view of the collection. <c>collection</c> is set to null and a 403 result
    /// is returned via <c>forbidden</c> when access is denied.
    /// </summary>
    private async Task<(CollectionMetadata? collection, IActionResult? forbidden)>
        AuthorizeCollectionAsync(CollectionMetadata collection, CancellationToken ct)
    {
        if (!_config.Auth.Enabled)
            return (collection, null);

        var roles = User.RoleNames();

        if (!await _auth.CanAccessCollectionAsync(roles, collection.Schema, collection.Name, ct))
        {
            return (null, StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Forbidden", collectionId = $"{collection.Schema}.{collection.Name}" }));
        }

        return (await _access.ApplyAsync(collection, User, ct), null);
    }

    [HttpGet("{collectionId}/items")]
    public async Task<IActionResult> GetItemsAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        OgcSupport.ValidateFormat(Request, features: true);
        var stage = Stopwatch.StartNew();
        var collection = await _featureService.GetCollectionAsync(collectionId, cancellationToken);
        RequestTiming.Add(HttpContext, "metadata", stage.Elapsed);

        if (collection is null)
        {
            return NotFound(new { error = "Collection not found", collectionId });
        }

        stage.Restart();
        var (authorized, forbidden) = await AuthorizeCollectionAsync(collection, cancellationToken);
        RequestTiming.Add(HttpContext, "authorize", stage.Elapsed);
        if (forbidden is not null) return forbidden;
        collection = authorized!;

        var queryDict = Request.Query.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());

        stage.Restart();
        FeatureQueryParameters parameters;
        try
        {
            parameters = FeatureQueryParametersParser.Parse(queryDict);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        FeatureQuery query;
        try
        {
            query = _queryBuilder.Build(collection, parameters, includeNext: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
        RequestTiming.Add(HttpContext, "query_build", stage.Elapsed);

        try
        {
            stage.Restart();
            var result = await _featureService.ExecuteAsync(
                query,
                cancellationToken);
            RequestTiming.Add(HttpContext, "db", stage.Elapsed);

            await using (result)
            {
                if (collection.IsSpatial)
                    Response.Headers["Content-Crs"] = $"<{OgcSupport.CrsUri(parameters.ResponseCrsSrid ?? 4326, OgcSupport.HasHeight(collection))}>";
                await JsonWriter.WriteAsync(
                    Response,
                    result,
                    cancellationToken,
                    new FeatureWriteOptions { ForceGeoJson = true, PageSize = query.PageSize,
                        Links = next => OgcSupport.PageLinks(Request, query.PageSize, query.Offset, next) });
            }

            return new EmptyResult();
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    [HttpGet("{collectionId}/queryables")]
    public async Task<IActionResult> GetQueryablesAsync(
    string collectionId,
    CancellationToken cancellationToken)
    {
        OgcSupport.ValidateFormat(Request, alternativeMediaType: "application/schema+json");
        var collection = await _featureService.GetCollectionAsync(collectionId, cancellationToken);

        if (collection is null)
        {
            return NotFound(new { error = "Collection not found", collectionId });
        }

        var (authorized, forbidden) = await AuthorizeCollectionAsync(collection, cancellationToken);
        if (forbidden is not null) return forbidden;
        collection = authorized!;

        var properties = new Dictionary<string, object>();

        foreach (var column in collection.Columns)
        {
            var item = new Dictionary<string, object>();

            var type = column.Type.ToLowerInvariant();

            if (type.Contains("int"))
            {
                item["type"] = "integer";
            }
            else if (type.StartsWith("numeric")
                || type.StartsWith("decimal")
                || type == "real"
                || type == "double precision")
            {
                item["type"] = "number";
            }
            else if (type == "boolean")
            {
                item["type"] = "boolean";
            }
            else if (type == "date")
            {
                item["type"] = "string";
                item["format"] = "date";
            }
            else if (type.Contains("timestamp"))
            {
                item["type"] = "string";
                item["format"] = "date-time";
            }
            else if (type == "uuid")
            {
                item["type"] = "string";
                item["format"] = "uuid";
            }
            else
            {
                item["type"] = "string";
            }

            if (collection.GeometryColumn is not null &&
                column.Name.Equals(collection.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                item["type"] = "object";
                item["format"] = "geometry";

                if (collection.GeometryType is not null)
                {
                    item["geometryType"] = collection.GeometryType;
                }

                if (collection.Srid.HasValue)
                {
                    item["srid"] = collection.Srid.Value;
                }
            }

            properties[column.Name] = item;
        }

        return new JsonResult(new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = OgcSupport.BaseUrl(Request) + Request.Path,
            ["title"] = $"{collection.Schema}.{collection.Name}",
            ["type"] = "object", ["properties"] = properties
        }) { ContentType = "application/schema+json" };
    }
    [HttpGet("{collectionId}/items/{id}")]
    public async Task<IActionResult> GetFeatureByIdAsync(
    string collectionId,
    string id,
    CancellationToken ct)
    {
        OgcSupport.ValidateFormat(Request, features: true);
        var collection = await _featureService.GetCollectionAsync(collectionId, ct);

        if (collection is null)
        {
            return NotFound(new
            {
                error = "Collection not found",
                collectionId
            });
        }

        var (authorized, forbidden) = await AuthorizeCollectionAsync(collection, ct);
        if (forbidden is not null) return forbidden;
        collection = authorized!;

        FeatureResult result;
        try
        {
            var srid = Request.Query.TryGetValue("crs", out var crs)
                ? FeatureQueryParametersParser.ParseCrs(crs.ToString(), "crs") : 4326;
            if (string.IsNullOrEmpty(collection.PrimaryKey))
                throw new InvalidOperationException("The collection does not have a visible single-column feature identifier.");
            result = await _featureService.ExecuteAsync(_queryBuilder.BuildFeatureById(collection, id, srid), ct);
            if (collection.IsSpatial) Response.Headers["Content-Crs"] = $"<{OgcSupport.CrsUri(srid, OgcSupport.HasHeight(collection))}>";
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        await using (result)
        {
            if (!result.Reader.HasRows)
            {
                return NotFound(new
                {
                    error = "Feature not found",
                    id
                });
            }

            await JsonWriter.WriteAsync(
                Response,
                result,
                ct, new FeatureWriteOptions { SingleFeature = true,
                    Links = _ => OgcSupport.PageLinks(Request, null, 0, false) });
        }

        return new EmptyResult();
    }
}
