using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PgDataHub.Auth;

namespace PgDataHub.Features;

[ApiController]
public sealed class SpatialAnalysisController(CollectionAccessService access, FeatureQueryBuilder builder, FeatureService features) : ControllerBase
{
    [HttpGet("/analysis/operations")]
    public IActionResult Operations() => Ok(SpatialAnalysis.Operations);

    [HttpPost("/collections/{collectionId}/analysis/{operation}")]
    public async Task<IActionResult> Execute(string collectionId, string operation,
        [FromBody] SpatialAnalysisRequest request, CancellationToken ct)
    {
        OgcSupport.ValidateFormat(Request, features: true);
        var collection = await access.GetAsync(collectionId, User, ct);
        if (collection is null) return NotFound();
        var parameters = FeatureQueryParametersParser.Parse(Request.Query.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString()));
        var query = builder.Build(collection, parameters, analysis: new(operation, request));
        try
        {
            await using var result = await features.ExecuteAsync(query, ct);
            Response.Headers["Content-Crs"] = $"<{OgcSupport.CrsUri(parameters.ResponseCrsSrid ?? (operation.Equals("transform", StringComparison.OrdinalIgnoreCase) ? request.TargetCrsSrid!.Value : 4326))}>";
            await JsonWriter.WriteAsync(Response, result, ct, new() { ForceGeoJson = true });
            return new EmptyResult();
        }
        catch (PostgresException ex) when (!Response.HasStarted &&
            (ex.SqlState.StartsWith("22", StringComparison.Ordinal) || ex.SqlState == PostgresErrorCodes.InternalError))
        {
            return BadRequest(new { error = "Invalid analysis geometry, CRS or topology for this operation." });
        }
    }
}
