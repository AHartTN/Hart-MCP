using System.Diagnostics;

namespace Hart.MCP.Api.Middleware;

/// <summary>
/// Middleware to add request timing and trace information
/// </summary>
public class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _logger;

    public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        
        context.Response.OnStarting(() =>
        {
            sw.Stop();
            context.Response.Headers["X-Request-Time-Ms"] = sw.ElapsedMilliseconds.ToString();
            context.Response.Headers["X-Trace-Id"] = traceId;
            return Task.CompletedTask;
        });

        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            
            if (sw.ElapsedMilliseconds > 1000) // Log slow requests
            {
                _logger.LogWarning(
                    "Slow request: {Method} {Path} took {ElapsedMs}ms. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path,
                    sw.ElapsedMilliseconds,
                    traceId);
            }
            else
            {
                _logger.LogDebug(
                    "Request: {Method} {Path} completed in {ElapsedMs}ms. Status: {StatusCode}",
                    context.Request.Method,
                    context.Request.Path,
                    sw.ElapsedMilliseconds,
                    context.Response.StatusCode);
            }
        }
    }
}

/// <summary>
/// Extension method to add request timing middleware
/// </summary>
public static class RequestTimingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestTiming(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestTimingMiddleware>();
    }
}
