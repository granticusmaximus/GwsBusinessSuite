using GwsBusinessSuite.Application.GovernmentIntelligence;

namespace GwsBusinessSuite.Web;

// Civic Watch's calendar subscription (20-11-26): meetings, elections, comment deadlines and the
// school board in Apple Calendar or Google, which fetch it without signing in, so the long random
// token in the address is the only key; an unknown one is a 404.
public static class CivicCalendarEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/civic/calendar/{token}.ics", async (string token, ICivicWatchService civic, IGovernmentIntelligenceService briefing,
            HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            GovernmentIntelligenceSnapshot? snapshot = null;
            try { snapshot = await briefing.GetSnapshotAsync(false, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the rest of the calendar still works */ }
            var ics = await civic.CalendarForTokenAsync(token, snapshot, cancellationToken);
            if (ics is null) return Results.Text("This calendar link isn't valid any more.", "text/plain", statusCode: StatusCodes.Status404NotFound);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Text(ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous().RequireRateLimiting("public-read");
    }
}
