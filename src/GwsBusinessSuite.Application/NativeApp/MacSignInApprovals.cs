using System.Security.Cryptography;
using System.Text;

namespace GwsBusinessSuite.Application.NativeApp;

public enum MacSignInState { Waiting, Approved, Declined, Expired, Used }

public sealed record MacSignInStart(Guid Id, string Secret, int Number, DateTimeOffset ExpiresAt);

// What the Mac app shows: the browser asking, and three numbers (one is on the browser's screen).
public sealed record MacSignInRequest(Guid Id, IReadOnlyList<int> Numbers, string Browser, string? Address, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt);

// "Approve with your Mac" (31-10-26, Grant: "if I log in with my fingerprint on my macOS, I want to
// be able to use that same for the web"). A browser passkey can't be shared with the ad-hoc-signed
// Mac app, so the browser asks the person's Mac instead: it shows a number, the Mac app (signed in
// as that person, with its Touch ID key) shows the request with three numbers, and picking the
// browser's one with Touch ID signs the browser in. Matching the number means a sign-in someone
// else started can't be approved by reflex: its number is only on their screen. Requests live two
// minutes, in memory (one server), and each signs in once.
public sealed class MacSignInApprovals(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _requests = [];

    private sealed class Entry
    {
        public required string Username { get; init; }
        public required string Secret { get; init; }
        public required int Number { get; init; }
        public required IReadOnlyList<int> Numbers { get; init; }
        public required string Browser { get; init; }
        public string? Address { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public MacSignInState State { get; set; }
    }

    // A newer request from anywhere replaces the person's one still waiting.
    public MacSignInStart Start(string username, string browser, string? address)
    {
        var number = RandomNumberGenerator.GetInt32(10, 100);
        var numbers = new List<int> { number };
        while (numbers.Count < 3)
        {
            var decoy = RandomNumberGenerator.GetInt32(10, 100);
            if (!numbers.Contains(decoy)) numbers.Add(decoy);
        }
        var entry = new Entry
        {
            Username = username.Trim(),
            Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Number = number,
            Numbers = numbers.OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).ToList(),
            Browser = browser,
            Address = address,
            StartedAt = time.GetUtcNow()
        };
        var id = Guid.NewGuid();
        lock (_gate)
        {
            Prune();
            foreach (var waiting in _requests.Values.Where(r => r.State == MacSignInState.Waiting && Same(r.Username, entry.Username)))
                waiting.State = MacSignInState.Declined;
            _requests[id] = entry;
        }
        return new MacSignInStart(id, entry.Secret, number, entry.StartedAt + Lifetime);
    }

    public MacSignInRequest? WaitingFor(string username)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            return _requests
                .Where(pair => pair.Value.State == MacSignInState.Waiting && Same(pair.Value.Username, username) && pair.Value.StartedAt + Lifetime > now)
                .OrderByDescending(pair => pair.Value.StartedAt)
                .Select(pair => new MacSignInRequest(pair.Key, pair.Value.Numbers, pair.Value.Browser, pair.Value.Address, pair.Value.StartedAt, pair.Value.StartedAt + Lifetime))
                .FirstOrDefault();
        }
    }

    // The Mac app's long poll: back as soon as a request is waiting, or after wait.
    public async Task<MacSignInRequest?> WaitForAsync(string username, TimeSpan wait, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + wait;
        while (true)
        {
            if (WaitingFor(username) is { } request) return request;
            if (time.GetUtcNow() >= until) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(500), time, cancellationToken);
        }
    }

    // Null when approved; otherwise why not. A wrong number declines the request outright.
    public string? Approve(Guid id, string username, int number)
    {
        lock (_gate)
        {
            if (Find(id, username) is not { } entry) return "That sign-in request has gone - start again in the browser.";
            if (entry.Number != number)
            {
                entry.State = MacSignInState.Declined;
                return "That isn't the number in the browser, so nothing was signed in.";
            }
            entry.State = MacSignInState.Approved;
            return null;
        }
    }

    public void Decline(Guid id, string username)
    {
        lock (_gate)
        {
            if (Find(id, username) is { } entry) entry.State = MacSignInState.Declined;
        }
    }

    // For the browser that asked (it alone has the secret).
    public MacSignInState State(Guid id, string secret)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var entry) || !SecretMatches(entry, secret)) return MacSignInState.Expired;
            if (entry.State == MacSignInState.Waiting && entry.StartedAt + Lifetime <= time.GetUtcNow()) entry.State = MacSignInState.Expired;
            return entry.State;
        }
    }

    // The username to sign the browser in as, once: null unless approved and not yet used.
    public string? Redeem(Guid id, string secret)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var entry) || !SecretMatches(entry, secret) || entry.State != MacSignInState.Approved) return null;
            if (entry.StartedAt + Lifetime <= time.GetUtcNow()) return null;
            entry.State = MacSignInState.Used;
            return entry.Username;
        }
    }

    // "Chrome on Windows" from a User-Agent, for the Mac to say who's asking.
    public static string DescribeBrowser(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Chrome/") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari" : "A browser";
        var system = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "a Mac" : ua.Contains("Linux") ? "Linux" : null;
        return system is null ? browser : $"{browser} on {system}";
    }

    private Entry? Find(Guid id, string username)
    {
        var now = time.GetUtcNow();
        return _requests.TryGetValue(id, out var entry) && entry.State == MacSignInState.Waiting && Same(entry.Username, username) && entry.StartedAt + Lifetime > now
            ? entry
            : null;
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Lifetime - Lifetime;
        foreach (var stale in _requests.Where(pair => pair.Value.StartedAt < cutoff).Select(pair => pair.Key).ToList()) _requests.Remove(stale);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SecretMatches(Entry entry, string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.Secret), Encoding.UTF8.GetBytes(secret ?? string.Empty));
}
