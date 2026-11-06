using System.Net;
using System.Text.Json;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// What every channel shares: asking for JSON or text and turning a platform's "no" into plain words.
public abstract class ReachChannel(IHttpClientFactory http) : IReachChannel
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Reads { get; }
    public virtual string? Searches => null;
    public virtual string? Needs => null;
    public abstract bool Handles(Uri url);
    public abstract Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct);
    public abstract Task CheckAsync(CancellationToken ct);

    public virtual Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct) =>
        throw new ReachException(Needs is { } needs ? $"Searching {Name} needs {needs}." : $"{Name} can't be searched from here; read a link instead.");

    protected async Task<JsonDocument> JsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? prepare = null, string client = ReachHttp.Fixed)
    {
        var text = await TextAsync(url, ct, prepare, client);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new ReachException($"{Name} didn't answer with data.");
        }
    }

    protected async Task<string> TextAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? prepare = null, string client = ReachHttp.Fixed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        prepare?.Invoke(request);
        return await SendAsync(request, ct, client);
    }

    protected async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct, string client = ReachHttp.Fixed)
    {
        using var response = await http.CreateClient(client).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw Refusal(response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected virtual ReachException Refusal(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new ReachException($"That isn't on {Name}, or it's private."),
        HttpStatusCode.Unauthorized => new ReachException($"{Name} turned the key down. Check {Needs ?? "its settings"}."),
        HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed => new ReachException($"{Name} refused the request, so it's left alone for a while."),
        HttpStatusCode.TooManyRequests => new ReachException($"{Name} is limiting how often we ask. Try again in a while."),
        _ => new ReachException($"{Name} answered {(int)status}.")
    };

    protected static string[] Segments(Uri url) => url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    protected static string? Query(Uri url, string name) =>
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == name && pair.Length == 2)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    protected static bool HostIs(Uri url, params string[] hosts) =>
        hosts.Any(host => url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}
