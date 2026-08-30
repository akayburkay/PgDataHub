using Microsoft.AspNetCore.Mvc;
using PgDataHub.Auth;
using PgDataHub.Config;

namespace PgDataHub.Metadata;
using System.Diagnostics;

[ApiController]
[Route("metadata")]
public sealed class MetadataController : ControllerBase
{
    private readonly MetadataService _metadata;
    private readonly AuthService _auth;
    private readonly PgDataHubConfig _config;

    public MetadataController(
        MetadataService metadata,
        AuthService auth,
        PgDataHubConfig config)
    {
        _metadata = metadata;
        _auth = auth;
        _config = config;
    }

    [HttpGet]
    [ProducesResponseType(
        typeof(MetadataResponse),
        StatusCodes.Status200OK)]
    public async Task<ActionResult<MetadataResponse>>
        Get(
            CancellationToken cancellationToken)
    {
        var stage = Stopwatch.StartNew();
        var collections = await _metadata.GetCollectionsAsync(cancellationToken);
        var functions = await _metadata.GetFunctionsAsync(cancellationToken);
        RequestTiming.Add(HttpContext, "metadata", stage.Elapsed);

        if (_config.Auth.Enabled)
        {
            stage.Restart();
            var roles = User.RoleNames();

            var visibleCollections = new List<CollectionMetadata>();
            foreach (var c in collections)
                if (await _auth.CanAccessCollectionAsync(roles, c.Schema, c.Name, cancellationToken))
                    visibleCollections.Add(await _auth.ApplyColumnRulesAsync(roles, c, cancellationToken));

            var visibleFunctions = new List<FunctionMetadata>();
            foreach (var f in functions)
                if (await _auth.CanAccessFunctionAsync(roles, f.Schema, f.Name, cancellationToken))
                    visibleFunctions.Add(f);

            collections = visibleCollections;
            functions = visibleFunctions;
            RequestTiming.Add(HttpContext, "authorize", stage.Elapsed);
        }

        return Ok(
            new MetadataResponse
            {
                Collections = collections,
                Functions = functions
            });
    }


    public sealed class MetadataResponse
    {
        public IReadOnlyList<CollectionMetadata>
            Collections
        { get; init; } = [];

        public IReadOnlyList<FunctionMetadata>
            Functions
        { get; init; } = [];
    }
}
