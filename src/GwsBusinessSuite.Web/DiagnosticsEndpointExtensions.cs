using System.Diagnostics;
using System.Threading.RateLimiting;
using GwsBusinessSuite.Application.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using LogContext = Serilog.Context.LogContext;

namespace GwsBusinessSuite.Web;

// Logging plan, Phase 1 (12-11-26): what ties a log line to the person, page and request behind
// it, and the endpoint that brings browser and Mac app errors into the same log.
public static class DiagnosticsEndpointExtensions
{
    public const string ClientErrorPolicy = "client-errors";
    public const string ClientErrorCategory = "GwsBusinessSuite.ClientErrors";

    // 30 reports a minute per signed-in user (or address): client-errors.js caps itself at 10 a
    // page, so this only stops a loop or a script from filling the log.
    public static void AddClientErrorLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(ClientErrorPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 30,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }));

    // After authentication: every line logged while handling a request names who made it.
    // ASP.NET already adds RequestId and RequestPath, and Serilog the Activity's TraceId.
    public static IApplicationBuilder UseUserLogContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var name = context.User.Identity is { IsAuthenticated: true } identity ? identity.Name : null;
            using (name is null ? null : LogContext.PushProperty("UserName", name))
            {
                await next(context);
            }
        });

    // The trace id of the request that served this page, for the gws-trace meta tag: a browser
    // error then links to the request (and its server-side lines) that rendered the page.
    public static string? CurrentTraceId() => Activity.Current?.TraceId.ToHexString();

    public static WebApplication MapDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/diagnostics/client", async (HttpContext context, ILoggerFactory loggers) =>
            {
                var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 16 * 1024;

                ClientErrorInput? input;
                try
                {
                    input = await context.Request.ReadFromJsonAsync<ClientErrorInput>(context.RequestAborted);
                }
                catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
                {
                    return Results.BadRequest();
                }

                if (ClientErrorReport.From(input) is not { } report) return Results.BadRequest();
                Log(loggers.CreateLogger(ClientErrorCategory), report, context.Request.Headers.UserAgent.ToString());
                return Results.NoContent();
            })
            .RequireAuthorization()
            .RequireRateLimiting(ClientErrorPolicy)
            .DisableAntiforgery()
            .ExcludeFromDescription();
        return app;
    }

    public static void Log(ILogger logger, ClientErrorReport report, string userAgent)
    {
        // Origin pushed here wins over the global "server" (FromLogContext enriches first).
        using (LogContext.PushProperty("Origin", report.Origin))
        using (report.Trace is null ? null : LogContext.PushProperty("ClientTraceId", report.Trace))
        {
            logger.LogWarning("Client {ClientKind} on {ClientPage}: {ClientMessage}{NewLine}{ClientStack} ({UserAgent})",
                report.Kind, report.Page ?? "(unknown page)", report.Message,
                report.Stack is null ? string.Empty : Environment.NewLine, report.Stack ?? string.Empty,
                LogRedactor.Redact(userAgent.Length <= 200 ? userAgent : userAgent[..200]));
        }
    }
}
