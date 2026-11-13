using System.Threading.RateLimiting;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.AspNetCore.Mvc;

namespace GwsBusinessSuite.Web;

// The World News tip line (trust plan N4, 13-11-26): anyone can send the desk a tip from /tip.
// Links only, no files; five tips per address per 15 minutes; the antiforgery token is checked;
// a hidden "website" field catches bots (filled in = quietly dropped).
public static class WorldNewsTipEndpoints
{
    public const string Policy = "world-news-tips";
    public const string Path = "/api/world-news/tips";

    public sealed record TipForm(string? Name, string? Contact, string? Message, string? Url, string? Website);

    public static void AddTipLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(Policy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(15),
                    PermitLimit = 5,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }));

    public static WebApplication MapTipEndpoints(this WebApplication app)
    {
        app.MapPost(Path, ([FromForm] TipForm form, WorldNewsService news) => Accept(form, news.Store, DateTimeOffset.UtcNow))
            .AllowAnonymous()
            .RequireRateLimiting(Policy)
            .ExcludeFromDescription();
        return app;
    }

    // Where to send the visitor next: back to /tip saying it was sent, or what to fix.
    public static IResult Accept(TipForm form, WorldNewsStore store, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(form.Website)) return Results.Redirect("/tip?sent=1");
        if (TipRules.Problem(form.Message, form.Url) is { } problem) return Results.Redirect("/tip?error=" + Uri.EscapeDataString(problem));
        store.AddTip(form.Name, form.Contact, form.Message!, form.Url, now);
        return Results.Redirect("/tip?sent=1");
    }
}
