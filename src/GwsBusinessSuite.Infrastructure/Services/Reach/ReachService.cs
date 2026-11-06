using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// Picks the channel for a link (Agent-Reach's "read <url>"), searches one channel, and checks them
// all (its "doctor"). A link no channel reads goes to the web reader (Ollama's web API) when that's set.
public sealed class ReachService(IEnumerable<IReachChannel> channels, ILogger<ReachService> logger, IOllamaWebSearchService? web = null)
{
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private readonly IReadOnlyList<IReachChannel> _channels = channels.ToList();

    public IReadOnlyList<IReachChannel> Channels => _channels;
    public bool ReadsWebPages => web is { IsConfigured: true };

    public IReachChannel? ChannelFor(Uri url) => _channels.FirstOrDefault(channel => channel.Handles(url));

    public async Task<ReachDocument> ReadAsync(string link, CancellationToken ct)
    {
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            throw new ReachException($"'{link}' isn't a web address.");
        if (ChannelFor(url) is { } channel)
        {
            var document = await Guarded(channel.Name, () => channel.ReadAsync(url, ct));
            if (document is not null) return document;
        }
        if (!ReadsWebPages)
            throw new ReachException($"No channel reads {url.Host} links, and reading other web pages needs OllamaWeb:ApiKey.");
        var page = await Guarded("the web", () => web!.FetchAsync(url.ToString(), ct));
        return new ReachDocument("Web", page.Title, page.Url, ReachText.Limit(page.Content, ReachText.TextCharacters));
    }

    public async Task<IReadOnlyList<ReachItem>> SearchAsync(string channelId, string query, string? within, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ReachException("Say what to search for.");
        var channel = _channels.FirstOrDefault(c => c.Id.Equals(channelId?.Trim(), StringComparison.OrdinalIgnoreCase)
                                                   || c.Name.Equals(channelId?.Trim(), StringComparison.OrdinalIgnoreCase))
                      ?? throw new ReachException($"There's no channel '{channelId}'. Channels: {string.Join(", ", _channels.Select(c => c.Id))}.");
        return await Guarded(channel.Name, () => channel.SearchAsync(query.Trim(), within, ct));
    }

    public IReadOnlyList<ReachStatus> Describe() =>
        _channels.Select(c => new ReachStatus(c.Id, c.Name, c.Reads, c.Searches, c.Needs)).ToList();

    // Every channel at once, each given CheckTimeout.
    public async Task<IReadOnlyList<ReachStatus>> CheckAllAsync(CancellationToken ct)
    {
        var checks = _channels.Select(async channel =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);
            string? problem = null;
            try
            {
                await Guarded(channel.Name, async () =>
                {
                    await channel.CheckAsync(timeout.Token);
                    return true;
                });
            }
            catch (ReachException ex)
            {
                problem = ex.Message;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                problem = $"{channel.Name} didn't answer within {CheckTimeout.TotalSeconds:0} seconds.";
            }
            return new ReachStatus(channel.Id, channel.Name, channel.Reads, channel.Searches, channel.Needs, Checked: true, Problem: problem);
        });
        return await Task.WhenAll(checks);
    }

    // The open client's guard (AutomationDestinationValidator) refuses private and reserved addresses
    // on connect; HttpClient hands that back inside an HttpRequestException.
    private static bool PointsInside(Exception ex) =>
        ex is InvalidOperationException && ex.Message.Contains("private", StringComparison.OrdinalIgnoreCase)
        || ex.InnerException is { } inner && PointsInside(inner);

    // Network trouble and refusals in plain words, so the model and the person see why.
    private async Task<T> Guarded<T>(string name, Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (HostPausedException ex)
        {
            throw new ReachException(ex.Message);
        }
        catch (HttpRequestException ex) when (PointsInside(ex))
        {
            throw new ReachException("That address points inside a private network, so it isn't read.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Reach: {Channel} couldn't be reached", name);
            throw new ReachException($"{name} couldn't be reached just now.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ReachException($"{name} answered with something unexpected.");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new ReachException($"{name} took too long to answer.");
        }
    }
}
