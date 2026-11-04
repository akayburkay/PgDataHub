using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PgDataHub;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;
    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;

        var errorId = context.TraceIdentifier;
        var (status, title, detail, level) = exception switch
        {
            BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status413PayloadTooLarge =>
                (413, "Request too large", "The request body exceeds the allowed size.", LogLevel.Information),
            BadHttpRequestException bad =>
                (bad.StatusCode, "Malformed request", "The HTTP request is malformed.", LogLevel.Information),
            ArgumentException => (400, "Invalid request", exception.Message, LogLevel.Information),
            PgDataHub.Features.UnsupportedRepresentationException =>
                (406, "Not acceptable", exception.Message, LogLevel.Information),
            InvalidOperationException => (400, "Invalid request", "The request is not valid for this resource.", LogLevel.Information),
            KeyNotFoundException => (404, "Resource not found", "The requested resource was not found.", LogLevel.Information),
            PgDataHub.Auth.CollectionAccessDeniedException =>
                (403, "Forbidden", "You do not have permission to access this collection.", LogLevel.Information),
            PostgresException p when p.SqlState == PostgresErrorCodes.ReadOnlySqlTransaction =>
                (403, "Read-only execution required", "The function attempted to write during read-only execution. Refresh function metadata before retrying.", LogLevel.Information),
            PostgresException p when p.SqlState.StartsWith("22", StringComparison.Ordinal) =>
                (400, "Invalid request", "One or more supplied values are invalid.", LogLevel.Information),
            _ => (500, "Unexpected error", "The request could not be completed.", LogLevel.Error)
        };

        _logger.Log(level, exception, "API request failed. ErrorId:{ErrorId} Method:{Method} Path:{Path}",
            errorId, context.Request.Method, context.Request.Path);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = title, Detail = detail,
            Instance = context.Request.Path,
            Extensions = { ["errorId"] = errorId }
        }, ct);
        return true;
    }
}
