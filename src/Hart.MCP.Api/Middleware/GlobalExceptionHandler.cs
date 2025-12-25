using Microsoft.AspNetCore.Diagnostics;
using System.Diagnostics;
using System.Text.Json;

namespace Hart.MCP.Api.Middleware;

/// <summary>
/// Global exception handler for consistent error responses
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        _logger.LogError(
            exception,
            "Unhandled exception occurred. TraceId: {TraceId}, Path: {Path}",
            traceId,
            httpContext.Request.Path);

        var (statusCode, errorResponse) = MapException(exception, traceId);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(errorResponse, cancellationToken);

        return true;
    }

    private (int StatusCode, ErrorDetails Response) MapException(Exception exception, string traceId)
    {
        return exception switch
        {
            ArgumentException argEx => (
                StatusCodes.Status400BadRequest,
                new ErrorDetails("Bad Request", argEx.Message, traceId)
            ),

            InvalidOperationException opEx when opEx.Message.Contains("not found") => (
                StatusCodes.Status404NotFound,
                new ErrorDetails("Not Found", opEx.Message, traceId)
            ),

            InvalidOperationException opEx => (
                StatusCodes.Status400BadRequest,
                new ErrorDetails("Invalid Operation", opEx.Message, traceId)
            ),

            OperationCanceledException => (
                StatusCodes.Status499ClientClosedRequest,
                new ErrorDetails("Request Cancelled", "The request was cancelled", traceId)
            ),

            TimeoutException => (
                StatusCodes.Status504GatewayTimeout,
                new ErrorDetails("Timeout", "The operation timed out", traceId)
            ),

            _ => (
                StatusCodes.Status500InternalServerError,
                new ErrorDetails(
                    "Internal Server Error",
                    _environment.IsDevelopment() ? exception.Message : "An unexpected error occurred",
                    traceId,
                    _environment.IsDevelopment() ? exception.StackTrace : null
                )
            )
        };
    }
}

/// <summary>
/// Standardized error response
/// </summary>
public record ErrorDetails(
    string Error,
    string Message,
    string TraceId,
    string? StackTrace = null
);

/// <summary>
/// Custom status code for client closed request
/// </summary>
public static class StatusCodesExtensions
{
    public const int Status499ClientClosedRequest = 499;
}
