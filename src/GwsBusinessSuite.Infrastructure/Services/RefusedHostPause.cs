using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// A site that refuses this server outright - 403 Forbidden or 405 Not Allowed to a plain request,
// the bot protection some sites put in front of datacenter addresses - is left alone for a day
// instead of being asked again every cycle. 05-11-26: Eventbrite answered 405, and gov.georgia.gov
// and senate.gov 403, to every request, about 450 times a day, and each refusal was a log warning.
// A refusal is respected, not worked around. Requests with credentials (an API token) are never
// paused, because their 403 means the token, not the server.
public sealed class RefusedHostPause(TimeProvider time)
{
    public static readonly TimeSpan Pause = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? PausedUntil(Uri url) =>
        _until.TryGetValue(url.Host, out var until) && time.GetUtcNow() < until ? until : null;

    public DateTimeOffset PauseHost(Uri url) => _until[url.Host] = time.GetUtcNow() + Pause;
}

public sealed class HostPausedException(string host, DateTimeOffset until)
    : HttpRequestException($"{host} refuses requests from this server, so it isn't asked again until {until:dd-MM-yy HH:mm} (UTC).");

public sealed class RefusedHostPauseHandler(RefusedHostPause pause, ILogger<RefusedHostPauseHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } url && pause.PausedUntil(url) is { } until)
            throw new HostPausedException(url.Host, until);

        var response = await base.SendAsync(request, cancellationToken);
        if (request.RequestUri is { } uri && request.Headers.Authorization is null
            && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
            var pausedUntil = pause.PauseHost(uri);
            logger.LogWarning("{Host} refused this server ({Status}); not asking it again until {Until:dd-MM-yy HH:mm} (UTC)",
                uri.Host, (int)response.StatusCode, pausedUntil);
        }
        return response;
    }
}
