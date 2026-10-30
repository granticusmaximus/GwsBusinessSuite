using System.Net.Http.Json;
using System.Text.Json;
#if MACCATALYST
using WebKit;
#endif

namespace GwsBusinessSuite.App;

public sealed record QuickNoteSaveResult(string? Url, string? Error);

// Native-side calls to the hosted app that need the signed-in session rather than a read-only
// Developer API key - today, saving a Quick Note (voice notes, screen captures). The session is
// whatever the Workspace tab's WebView is signed in with (device login or the normal sign-in),
// read from its cookie store, so there's no second sign-in and nothing new stored.
public sealed record ContactCreateResult(string? Url, string? FullName, string? Error);

// One model call for SentinelGPT's agent, relayed from a browser elsewhere (SentinelAgentMacRelay).
public sealed record AgentChatJob(Guid Id, string PayloadJson);

// An agent turn started from the SentinelGPT tab, as the server reports it while it runs.
public sealed record AgentTurnSnapshot(string Status, IReadOnlyList<string> Steps, IReadOnlyList<AgentActionItem> Actions,
    string? Answer, string? Model, string? Error, string GoMessage)
{
    public bool IsRunning => Status == "running";
    public bool IsFailed => Status == "failed";
}

// One change or proposal the agent made (the server's SentinelAgentActionView), as a card.
public sealed record AgentActionItem(Guid Id, string Tool, string Summary, string Status, string? Link, bool CanUndo, bool AwaitingConfirm)
{
    public bool IsPlan => Tool == "show_plan";
    public string Title => (IsPlan ? "Plan\n" : string.Empty) + Summary + Status switch
    {
        "undone" => " (undone)",
        "declined" => IsPlan ? " (cancelled)" : " (declined)",
        "done" when IsPlan => " (started)",
        _ => string.Empty
    };
    public string ConfirmLabel => IsPlan ? "Go" : "Confirm";
    public string DeclineLabel => IsPlan ? "Cancel" : "Decline";
    public bool ShowOpen => Link is not null && !AwaitingConfirm && Status != "undone";
    public Color BorderColor => AwaitingConfirm ? Color.FromArgb("#F59E0B") : Status == "done" ? Color.FromArgb("#15803D") : Color.FromArgb("#57534E");
}

// A World News event due a summary, with the prompt to answer (WorldNewsSummaryRelay).
public sealed record WorldNewsSummaryJob(string EventId, string Title, int ArticleCount, string SystemPrompt, string Prompt);

// A World News headline waiting for its embedding (WorldNewsEmbedRelay).
public sealed record WorldNewsEmbedJob(string Id, string Title);

public sealed class NativeSessionClient(HttpClient httpClient)
{
    private const string SignInFirst = "Sign in on the Workspace tab first - this uses that session.";
    private const string SessionExpired = "Your session has expired - sign in again on the Workspace tab, then retry.";
    private const string Unreachable = "Couldn't reach the server.";

    // Errors that a later retry can fix (offline, or no/expired session) - the Quick Note outbox
    // keeps a note for these instead of failing it.
    public static bool IsRetryable(string error) => error is SignInFirst or SessionExpired or Unreachable;

    // Offline reading (Sentinel item 12): copies of the pages this person keeps offline.
    public async Task<(IReadOnlyList<(OfflinePageEntry Entry, string Html)>? Pages, string? Error)> GetOfflinePagesAsync(CancellationToken cancellationToken = default)
    {
        var (json, error) = await GetJsonAsync("/admin/api/native/offline-pages", cancellationToken);
        if (json is not { ValueKind: JsonValueKind.Array } list) return (null, error ?? "The server sent something unexpected.");
        var pages = list.EnumerateArray().Select(item => (
            new OfflinePageEntry(
                item.GetProperty("id").GetGuid(),
                item.GetProperty("title").GetString() ?? "Untitled",
                item.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null,
                item.GetProperty("updatedAtUnix").GetInt64()),
            item.GetProperty("html").GetString() ?? string.Empty)).ToList();
        return (pages, null);
    }

    // toDailyNote adds it to today's daily note instead of making a Quick Notes page.
    // contactId/bookingId link the note to a CRM contact or calendar booking (meeting notes); a
    // linked note is always its own page, never added to the daily note.
    public async Task<QuickNoteSaveResult> SaveQuickNoteAsync(string title, string markdown, bool toDailyNote = false,
        Guid? contactId = null, Guid? bookingId = null, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/quick-notes", new { title, markdown, dailyNote = toDailyNote, contactId, bookingId }, cancellationToken);
        return json is { } body ? new(AppEndpoints.BaseUrl + body.GetProperty("url").GetString(), null) : new(null, error);
    }

    // World News events whose summaries this Mac's local model writes.
    public async Task<(IReadOnlyList<WorldNewsSummaryJob>? Jobs, string? Error)> GetWorldNewsSummaryJobsAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        var (json, error) = await GetJsonAsync($"/admin/api/native/world-news/summary-jobs?limit={limit}", cancellationToken);
        if (json is not { ValueKind: JsonValueKind.Array } list) return (null, error ?? "The server sent something unexpected.");
        return (list.EnumerateArray().Select(item => new WorldNewsSummaryJob(
            item.GetProperty("eventId").GetString() ?? string.Empty,
            item.GetProperty("title").GetString() ?? string.Empty,
            item.GetProperty("articleCount").GetInt32(),
            item.GetProperty("system").GetString() ?? string.Empty,
            item.GetProperty("prompt").GetString() ?? string.Empty)).ToList(), null);
    }

    // Null once the server has saved the summary, otherwise what went wrong.
    public async Task<string?> SaveWorldNewsSummaryAsync(WorldNewsSummaryJob job, string model, string answer,
        CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/world-news/summaries",
            new { eventId = job.EventId, model, answer, articleCount = job.ArticleCount }, cancellationToken);
        return json is null ? error : null;
    }

    // World News headlines this Mac embeds, and the model the server groups them with. No headlines
    // unless one of models (what this Mac has installed) is that model.
    public async Task<(string? Model, IReadOnlyList<WorldNewsEmbedJob>? Jobs, string? Error)> GetWorldNewsEmbedJobsAsync(
        IEnumerable<string> models, int limit, CancellationToken cancellationToken = default)
    {
        var (json, error) = await GetJsonAsync(
            $"/admin/api/native/world-news/embed-jobs?limit={limit}&models={Uri.EscapeDataString(string.Join(',', models))}", cancellationToken);
        if (json is not { ValueKind: JsonValueKind.Object } body) return (null, null, error ?? "The server sent something unexpected.");
        return (body.GetProperty("model").GetString(), body.GetProperty("jobs").EnumerateArray()
            .Select(item => new WorldNewsEmbedJob(item.GetProperty("id").GetString() ?? string.Empty, item.GetProperty("title").GetString() ?? string.Empty))
            .ToList(), null);
    }

    // Null once the server has saved the embeddings, otherwise what went wrong.
    public async Task<string?> SaveWorldNewsEmbeddingsAsync(string model, IReadOnlyList<(string Id, float[] Vector)> embeddings,
        CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/world-news/embeddings",
            new { model, items = embeddings.Select(embedding => new { id = embedding.Id, vector = embedding.Vector }) }, cancellationToken);
        return json is null ? error : null;
    }

    // SentinelGPT's agent relay (30-10-26): the server's next model call for this person, after a
    // long poll; (null, null) when there was none this time.
    public async Task<(AgentChatJob? Job, string? Error)> TakeAgentChatJobAsync(IEnumerable<string> models, CancellationToken cancellationToken = default)
    {
        using var request = await SessionRequestAsync(HttpMethod.Get,
            "/admin/api/native/agent/chat-jobs?models=" + Uri.EscapeDataString(string.Join(',', models)));
        if (request is null) return (null, SignInFirst);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return (null, null);
            // An expired session redirects to the sign-in page, which answers 200 with HTML.
            if (response.Content.Headers.ContentType?.MediaType != "application/json") return (null, SessionExpired);
            if (!response.IsSuccessStatusCode) return (null, $"The server said no (HTTP {(int)response.StatusCode}).");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            return (new AgentChatJob(root.GetProperty("id").GetGuid(), root.GetProperty("payload").GetString() ?? "{}"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return (null, Unreachable);
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // The SentinelGPT tab's agent turns (30-10-26): started and followed on the server, thinking on
    // this Mac through the relay. No id means "not available"; the tab then answers locally.
    public async Task<(Guid? Id, string? Error)> StartAgentTurnAsync(Guid conversationId, string message, string? model, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/agent/turns", new { conversationId, message, model }, cancellationToken);
        return json is { } root && root.TryGetProperty("id", out var id) && id.TryGetGuid(out var turnId) ? (turnId, null) : (null, error);
    }

    public async Task<(AgentTurnSnapshot? Turn, string? Error)> GetAgentTurnAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        var (json, error) = await GetJsonAsync($"/admin/api/native/agent/turns/{turnId}", cancellationToken);
        try
        {
            return json is { } root ? (root.Deserialize<AgentTurnSnapshot>(WebJson), null) : (null, error);
        }
        catch (JsonException)
        {
            return (null, "The server sent something unexpected.");
        }
    }

    public async Task<(AgentActionItem? Action, string? Error)> ResolveAgentActionAsync(Guid actionId, string verb, bool approve,
        CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync($"/admin/api/native/agent/actions/{actionId}/{verb}", new { approve }, cancellationToken);
        try
        {
            return json is { } root ? (root.Deserialize<AgentActionItem>(WebJson), null) : (null, error);
        }
        catch (JsonException)
        {
            return (null, "The server sent something unexpected.");
        }
    }

    public Task StopAgentTurnAsync(Guid turnId) => PostAsync($"/admin/api/native/agent/turns/{turnId}/stop", new { }, CancellationToken.None);

    // Null once the server has the reply, otherwise what went wrong.
    public async Task<string?> ReplyAgentChatJobAsync(AgentChatJob job, string? body, string? error, CancellationToken cancellationToken = default)
    {
        var (json, failure) = await PostAsync($"/admin/api/native/agent/chat-jobs/{job.Id}", new { body, error }, cancellationToken);
        return json is null ? failure : null;
    }

    // Bookings around today and recent contacts a meeting note can be linked to.
    public async Task<IReadOnlyList<MeetingTarget>> GetMeetingTargetsAsync(CancellationToken cancellationToken = default)
    {
        var (json, _) = await GetJsonAsync("/admin/api/native/meeting-targets", cancellationToken);
        if (json is not { ValueKind: JsonValueKind.Array } list) return [];
        return list.EnumerateArray().Select(item => new MeetingTarget(
            item.GetProperty("kind").GetString() ?? string.Empty,
            item.GetProperty("id").GetGuid(),
            item.GetProperty("label").GetString() ?? string.Empty,
            item.GetProperty("detail").GetString() ?? string.Empty)).ToList();
    }

    public async Task<ContactCreateResult> CreateContactAsync(string fullName, string email, string company, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/contacts", new { fullName, email, company }, cancellationToken);
        return json is { } body
            ? new(AppEndpoints.BaseUrl + body.GetProperty("url").GetString(), body.GetProperty("fullName").GetString(), null)
            : new(null, null, error);
    }

    // ---- Page lock: Touch ID in the Mac app (server's SentinelDeviceKeyService) ----

    public async Task<(Guid? KeyId, string? Username, string? Error)> RegisterTouchIdKeyAsync(string name, byte[] publicKeySpki, string storage, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/lock-device",
            new { name, publicKey = Convert.ToBase64String(publicKeySpki), storage }, cancellationToken);
        return json is { } body ? (body.GetProperty("id").GetGuid(), body.GetProperty("username").GetString(), null) : (null, null, error);
    }

    public async Task<(string? Challenge, string? Error)> GetLockChallengeAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        var (json, error) = await PostAsync("/admin/api/native/lock-challenge", new { keyId }, cancellationToken);
        return json is { } body ? (body.GetProperty("challenge").GetString(), null) : (null, error);
    }

    public async Task<string?> UnlockWithTouchIdAsync(Guid keyId, byte[] signature, CancellationToken cancellationToken = default)
    {
        var (_, error) = await PostAsync("/admin/api/native/lock-unlock", new { keyId, signature = Convert.ToBase64String(signature) }, cancellationToken);
        return error;
    }

    // What the web view's session is: (signed in, Mac-app sign-in that Content Studio needs).
    public async Task<(bool SignedIn, bool MacApp, string? Method)> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        var (json, _) = await GetJsonAsync("/admin/api/native/session-info", cancellationToken);
        if (json is not { } body) return (false, false, null);
        return (true, body.GetProperty("macApp").GetBoolean(), body.TryGetProperty("amr", out var amr) ? amr.GetString() : null);
    }

    // Confirms it's the owner (their page-lock PIN) before another way to unlock is added.
    public async Task<string?> UnlockWithPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        var (_, error) = await PostAsync("/admin/api/native/lock-unlock-pin", new { pin }, cancellationToken);
        return error;
    }

    // The app was hidden: this session's locked pages lock again.
    public async Task LockSessionAsync(CancellationToken cancellationToken = default) =>
        await PostAsync("/admin/api/native/lock-session", new { }, cancellationToken);

    // ---- Camera watch (the server's CameraWatchService) ----

    public async Task<(JsonElement? Json, string? Error)> GetCameraWatchListAsync(CancellationToken cancellationToken = default) =>
        await GetJsonAsync("/admin/api/native/overwatch/watch-list", cancellationToken);

    public async Task<byte[]?> GetCameraWatchSnapshotAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        using var request = await SessionRequestAsync(HttpMethod.Get, $"/admin/api/native/overwatch/watch-snapshot?id={Uri.EscapeDataString(cameraId)}");
        if (request is null) return null;
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true) return null;
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<string?> PostCameraWatchReadingAsync(string cameraId, string modelOutput, byte[] frame, CancellationToken cancellationToken = default)
    {
        var (_, error) = await PostAsync("/admin/api/native/overwatch/watch-reading",
            new { cameraId, modelOutput, imageBase64 = Convert.ToBase64String(frame) }, cancellationToken);
        return error;
    }

    private async Task<(JsonElement? Json, string? Error)> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = await SessionRequestAsync(HttpMethod.Get, path);
        if (request is null) return (null, SignInFirst);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, SessionExpired);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return response.IsSuccessStatusCode ? (document.RootElement.Clone(), null) : (null, $"The server said no (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, Unreachable);
        }
    }

    private static async Task<HttpRequestMessage?> SessionRequestAsync(HttpMethod method, string path)
    {
        var cookieHeader = await GetSessionCookieHeaderAsync();
        if (string.IsNullOrEmpty(cookieHeader)) return null;
        var request = new HttpRequestMessage(method, AppEndpoints.BaseUrl + path);
        request.Headers.Add("Cookie", cookieHeader);
        request.Headers.Add("X-GWS-Native", "1");
        return request;
    }

    // POSTs JSON with the WebView's session; the parsed JSON on success, or a message to show.
    private async Task<(JsonElement? Json, string? Error)> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        var cookieHeader = await GetSessionCookieHeaderAsync();
        if (string.IsNullOrEmpty(cookieHeader))
            return (null, SignInFirst);

        using var request = new HttpRequestMessage(HttpMethod.Post, AppEndpoints.BaseUrl + path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Cookie", cookieHeader);
        request.Headers.Add("X-GWS-Native", "1");
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // An expired session redirects to the sign-in page, which answers 200 with HTML.
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, SessionExpired);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();
            if (!response.IsSuccessStatusCode)
            {
                return (null, root.TryGetProperty("error", out var error) ? error.GetString() : $"The server said no (HTTP {(int)response.StatusCode}).");
            }
            return (root, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, Unreachable);
        }
    }

    private static async Task<string?> GetSessionCookieHeaderAsync()
    {
#if MACCATALYST
        if (!Uri.TryCreate(AppEndpoints.BaseUrl, UriKind.Absolute, out var baseUri)) return null;
        var cookies = await WKWebsiteDataStore.DefaultDataStore.HttpCookieStore.GetAllCookiesAsync();
        var mine = cookies
            .Where(c => baseUri.Host.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Name}={c.Value}")
            .ToList();
        return mine.Count == 0 ? null : string.Join("; ", mine);
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}

public sealed record MeetingTarget(string Kind, Guid Id, string Label, string Detail)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Detail) ? Label : $"{Label} \u00b7 {Detail}";
}
