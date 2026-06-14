using Microsoft.AspNetCore.Mvc;

namespace PgDataHub.Metadata;

[ApiController]
[Route("metadata")]
public sealed class MetadataController : ControllerBase
{
    private readonly MetadataService _metadata;

    public MetadataController(
        MetadataService metadata)
    {
        _metadata = metadata;
    }

    [HttpGet]
    [ProducesResponseType(
        typeof(MetadataResponse),
        StatusCodes.Status200OK)]
    public async Task<ActionResult<MetadataResponse>>
        Get(
            CancellationToken cancellationToken)
    {
        return Ok(
            new MetadataResponse
            {
                Collections =
                    await _metadata.GetCollectionsAsync(
                        cancellationToken),

                Functions =
                    await _metadata.GetFunctionsAsync(
                        cancellationToken)
            });
    }

    [HttpPost("refresh")]
    [ProducesResponseType(
        StatusCodes.Status204NoContent)]
    public async Task<IActionResult>
        Refresh(
            CancellationToken cancellationToken)
    {
        await _metadata.RefreshAsync(
            cancellationToken);

        return NoContent();
    }
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