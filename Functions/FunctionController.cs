using Microsoft.AspNetCore.Mvc;
using Npgsql;
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

    [HttpGet("{oid:long}")]
    public async Task<ActionResult<FunctionMetadata>> GetByOid(uint oid, CancellationToken ct)
    {
        var function = await _functions.GetFunctionAsync(oid, ct);
        if (function is null)
            return NotFound(new { error = "Function not found." });
        if (await ForbidFunctionAsync(function.Schema, function.Name, ct) is { } forbidden)
            return forbidden;
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
    [HttpPost("{schema}.{name}/items")]
    public async Task<IActionResult> GetItems(
        string schema,
        string name,
        CancellationToken ct)
    {
        if (await ForbidFunctionAsync(schema, name, ct) is { } forbidden)
            return forbidden;

        try
        {
            var function = await _functions.GetFunctionAsync(schema, name, ct);
            if (function is null)
                return NotFound(new { error = "Function not found or its name is ambiguous; use the function OID endpoint for overloads." });
            return await ExecuteItemsAsync(function, ct);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            return BadRequest(new { error = "One or more function parameter values are invalid." });
        }
    }

    [HttpGet("{oid:long}/items")]
    [HttpPost("{oid:long}/items")]
    public async Task<IActionResult> GetItemsByOid(uint oid, CancellationToken ct)
    {
        var function = await _functions.GetFunctionAsync(oid, ct);
        if (function is null)
            return NotFound(new { error = "Function not found." });
        if (await ForbidFunctionAsync(function.Schema, function.Name, ct) is { } forbidden)
            return forbidden;

        try
        {
            return await ExecuteItemsAsync(function, ct);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            return BadRequest(new { error = "One or more function parameter values are invalid." });
        }
    }

    private async Task<IActionResult> ExecuteItemsAsync(FunctionMetadata function, CancellationToken ct)
    {
        var post = HttpMethods.IsPost(Request.Method);
        if (function.IsVolatile)
        {
            if (!post)
            {
                Response.Headers.Allow = "POST";
                return StatusCode(StatusCodes.Status405MethodNotAllowed,
                    new { error = "VOLATILE functions require POST and a separate execution permission." });
            }
            // Public/default roles must never acquire write access implicitly.
            if (!_config.Auth.Enabled || User.Identity?.IsAuthenticated != true ||
                User.HasClaim("pgdatahub:source", nameof(PrincipalSource.Anonymous)) ||
                !await _auth.CanExecuteVolatileFunctionAsync(User.RoleNames(), function.Schema, function.Name, ct))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "Explicit volatile function execution permission is required." });
        }

        await _functions.ExecuteAsync(function, Request.Query, Response, ct, allowVolatile: post && function.IsVolatile);
        return new EmptyResult();
    }
}
