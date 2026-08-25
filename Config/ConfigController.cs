using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PgDataHub.Auth;

namespace PgDataHub.Config;

[ApiController, AllowAnonymous]
[Route("admin/config")]
public sealed class ConfigController : AdminControllerBase
{
    private readonly ConfigService _service;
    private readonly Metadata.MetadataService _metadata;
    public ConfigController(ConfigService service, PgDataHubConfig config, AdminSessionService sessions,
        Metadata.MetadataService metadata) : base(config, sessions)
    { _service = service; _metadata = metadata; }

    [HttpGet]
    public IActionResult Get()
    {
        if (AdminGuard() is { } guard) return guard;
        return Ok(_service.Get());
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] ConfigUpdate request, CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;
        try { return Ok(await _service.SaveAsync(request, ct)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup(CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;
        return Ok(await _metadata.GetAdminLookupAsync(ct));
    }

    [HttpGet("metadata-listener")]
    public async Task<IActionResult> Listener(CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;
        return Ok(await _metadata.GetListenerStatusAsync(ct));
    }

    [HttpPost("metadata-refresh")]
    public async Task<IActionResult> RefreshMetadata(CancellationToken ct)
    {
        if (AdminGuard() is { } guard) return guard;
        await _metadata.RefreshAsync(ct);
        return NoContent();
    }
}
