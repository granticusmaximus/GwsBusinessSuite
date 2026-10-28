using GwsBusinessSuite.Application.Scheduling;

namespace GwsBusinessSuite.Web;

// The private iCal feed of confirmed bookings (Scheduling > Calendar sync). Google Calendar and
// the Calendar app fetch it on their own schedule, without signing in, so the long random token
// in the address is the only key; an unknown or turned-off one is a 404.
public static class BookingCalendarEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/bookings/calendar/{token}.ics", async (string token, IBookingCalendarService calendar, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var ics = await calendar.RenderExportAsync(token, cancellationToken);
            // A body keeps the 404 plain: an empty one is re-executed to the site's not-found
            // page, which on the admin host sends an anonymous caller to the login page.
            if (ics is null) return Results.Text("This calendar link isn't valid any more.", "text/plain", statusCode: StatusCodes.Status404NotFound);
            httpContext.Response.Headers.CacheControl = "private, no-store";
            httpContext.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Text(ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous().RequireRateLimiting("public-read");
    }
}
