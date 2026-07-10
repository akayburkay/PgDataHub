using Microsoft.AspNetCore.Mvc;
using PgDataHub.Auth;
using PgDataHub.Config;
using PgDataHub.Metadata;

namespace PgDataHub.Functions;

[ApiController]
[Route("functions")]
public sealed class FunctionController : ControllerBase
{
    private readonly FunctionService _functions;
    private readonly AuthService _auth;
    private readonly PgDataHubConfig _config;

    public FunctionController(
        FunctionService functions,
        AuthService auth,
        PgDataHubConfig config)
    {
        _functions = functions;
        _auth = auth;
        _config = config;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FunctionMetadata>>> Get(CancellationToken ct)
    {
        var functions = await _functions.GetFunctionsAsync(ct);

        if (_config.Auth.Enabled)
        {
            var roles = User.RoleNames();
            var allowed = new List<FunctionMetadata>();
            foreach (var f in functions)
                if (await _auth.CanAccessFunctionAsync(roles, f.Schema, f.Name, ct))
                    allowed.Add(f);
            functions = allowed;
        }

        return Ok(functions);
    }

    [HttpGet("{schema}.{name}")]
    public async Task<ActionResult<FunctionMetadata>> GetById(
        string schema,
        string name,
        CancellationToken ct)
    {
        if (await ForbidFunctionAsync(schema, name, ct) is { } forbidden)
            return forbidden;

        var function = await _functions.GetFunctionAsync(schema, name, ct);

        if (function is null)
            return NotFound(new { error = "Function not found." });

        return Ok(function);
    }

    /// <summary>Returns a 403 result when the caller may not access the function; otherwise null.</summary>
    private async Task<ActionResult?> ForbidFunctionAsync(
        string schema, string name, CancellationToken ct)
    {
        if (!_config.Auth.Enabled)
            return null;

        if (await _auth.CanAccessFunctionAsync(User.RoleNames(), schema, name, ct))
            return null;

        return StatusCode(
            StatusCodes.Status403Forbidden,
            new { error = "Forbidden", function = $"{schema}.{name}" });
    }

    [HttpGet("{schema}.{name}/items")]
    public async Task<IActionResult> GetItems(
        string schema,
        string name,
        CancellationToken ct)
    {
        if (await ForbidFunctionAsync(schema, name, ct) is { } forbidden)
            return forbidden;

        try
        {
            await _functions.ExecuteAsync(
                schema,
                name,
                Request.Query,
                Response,
                ct);

            return new EmptyResult();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}