using Microsoft.AspNetCore.Mvc;
using PgDataHub.Metadata;

namespace PgDataHub.Functions;

[ApiController]
[Route("functions")]
public sealed class FunctionController : ControllerBase
{
    private readonly FunctionService _functions;

    public FunctionController(FunctionService functions)
    {
        _functions = functions;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FunctionMetadata>>> Get(CancellationToken ct)
        => Ok(await _functions.GetFunctionsAsync(ct));

    [HttpGet("{schema}.{name}")]
    public async Task<ActionResult<FunctionMetadata>> GetById(
        string schema,
        string name,
        CancellationToken ct)
    {
        var function = await _functions.GetFunctionAsync(schema, name, ct);

        if (function is null)
            return NotFound(new { error = "Function not found." });

        return Ok(function);
    }

    [HttpGet("{schema}.{name}/items")]
    public async Task<IActionResult> GetItems(
        string schema,
        string name,
        CancellationToken ct)
    {
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