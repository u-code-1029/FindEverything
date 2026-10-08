using System.Text.Json;
using FindEverything.Server.Models;

namespace FindEverything.Server.Api;

public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            var (status, title) = error switch
            {
                ServerNotFoundException => (StatusCodes.Status404NotFound, "Resource not found."),
                ServerConflictException => (StatusCodes.Status409Conflict, "The resource changed or conflicts with another operation."),
                ServerQueueFullException => (StatusCodes.Status429TooManyRequests, "The scan queue is full."),
                ArgumentException or JsonException => (StatusCodes.Status400BadRequest, "Invalid request."),
                BadHttpRequestException bad => (bad.StatusCode, "Invalid request."),
                OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Request cancelled."),
                _ => (StatusCodes.Status500InternalServerError, "The request could not be completed.")
            };
            if (status >= 500) logger.LogError(error, "API request failed. Trace {TraceId}", context.TraceIdentifier);
            context.Response.Clear();
            context.Response.StatusCode = status;
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
            await Results.Problem(statusCode: status, title: title,
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        }
    }
}
