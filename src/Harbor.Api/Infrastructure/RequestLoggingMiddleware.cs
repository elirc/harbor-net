using System.Diagnostics;

namespace Harbor.Api.Infrastructure;

/// <summary>
/// Logs one line per request: method, path, status, duration, and who made it.
///
/// The request id is echoed back in X-Request-Id so a caller reporting a
/// problem can hand over the exact identifier that appears in the logs.
/// Deliberately logs the teammate and workspace ids rather than the API key or
/// any body: enough to trace a request, nothing worth stealing from a log file.
/// </summary>
public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public const string RequestIdHeader = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        // Honour an upstream id when there is one, so a trace survives a proxy.
        var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault()
            ?? context.TraceIdentifier;
        context.Response.Headers[RequestIdHeader] = requestId;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();

            var user = context.User;
            var teammateId = user.Identity?.IsAuthenticated == true
                ? user.GetTeammateId().ToString()
                : "anonymous";
            var workspaceId = user.Identity?.IsAuthenticated == true
                ? user.GetWorkspaceId().ToString()
                : "-";

            // Server faults are the operator's problem; everything else is
            // ordinary traffic, including the 4xx a client brought on itself.
            var level = context.Response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Information;

            logger.Log(
                level,
                "{Method} {Path} -> {StatusCode} in {ElapsedMs}ms "
                + "(request {RequestId}, teammate {TeammateId}, workspace {WorkspaceId})",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                requestId,
                teammateId,
                workspaceId);
        }
    }
}
