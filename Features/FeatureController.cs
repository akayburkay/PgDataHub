using Microsoft.AspNetCore.Mvc;

namespace PgDataHub.Features;

[ApiController]
[Route("collections")]
public sealed class FeatureController : ControllerBase
{
    private readonly FeatureService _featureService;
    private readonly FeatureQueryBuilder _queryBuilder;

    public FeatureController(
        FeatureService featureService,
        FeatureQueryBuilder queryBuilder)
    {
        _featureService = featureService;
        _queryBuilder = queryBuilder;
    }

    [HttpGet("{collectionId}/items")]
    public async Task<IActionResult> GetItemsAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        var collection = await _featureService.GetCollectionAsync(collectionId, cancellationToken);

        if (collection is null)
        {
            return NotFound(new { error = "Collection not found", collectionId });
        }

        var queryDict = Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

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

        try
        {
            var result = await _featureService.ExecuteAsync(
                query,
                cancellationToken);

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
}