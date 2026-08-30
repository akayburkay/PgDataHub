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

    public FeatureController(
        FeatureService featureService,
        FeatureQueryBuilder queryBuilder,
        AuthService auth,
        PgDataHubConfig config)
    {
        _featureService = featureService;
        _queryBuilder = queryBuilder;
        _auth = auth;
        _config = config;
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

        return (await _auth.ApplyColumnRulesAsync(roles, collection, ct), null);
    }

    [HttpGet("{collectionId}/items")]
    public async Task<IActionResult> GetItemsAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
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
            query = _queryBuilder.Build(collection, parameters);
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
                await JsonWriter.WriteAsync(
                    Response,
                    result,
                    cancellationToken);
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

        return Ok(new
        {
            title = $"{collection.Schema}.{collection.Name}",
            type = "object",
            properties
        });
    }
    [HttpGet("{collectionId}/items/{id}")]
    public async Task<IActionResult> GetFeatureByIdAsync(
    string collectionId,
    string id,
    CancellationToken ct)
    {
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
            result = await _featureService.GetFeatureByIdAsync(collection, id, ct);
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
                ct);
        }

        return new EmptyResult();
    }
}
