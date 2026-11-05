using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Domain.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record MailFetched(MimeMessage Message, string? ThreadId, bool Unread, bool Starred);

// One provider's way of reaching a mailbox for MailService. Ids are the provider's own (Gmail's
// message id; for IMAP the folder, UIDVALIDITY and UID - see ImapMessageId).
public interface IMailboxBackend
{
    string Provider { get; }
    // Signs in before an account is saved (IMAP and SMTP); throws MailException. Returns a warning,
    // not an error, when reading works but this server can't reach the SMTP port at all (a host
    // blocking outgoing mail ports): the mailbox is still worth having, and sending starts working
    // with no reconnect once the port opens.
    Task<string?> VerifyAsync(MailAccount account, string password, CancellationToken cancellationToken);
    Task<MailPage> ListAsync(MailAccount account, string folder, string? search, string? pageToken, CancellationToken cancellationToken);
    Task<MailFetched> GetAsync(MailAccount account, string id, bool markRead, CancellationToken cancellationToken);
    Task SendAsync(MailAccount account, MimeMessage message, string? threadId, CancellationToken cancellationToken);
    Task SetReadAsync(MailAccount account, string id, bool read, CancellationToken cancellationToken);
    Task SetStarredAsync(MailAccount account, string id, bool starred, CancellationToken cancellationToken);
    Task ArchiveAsync(MailAccount account, string id, CancellationToken cancellationToken);
    Task TrashAsync(MailAccount account, string id, CancellationToken cancellationToken);
    Task<int> UnreadCountAsync(MailAccount account, CancellationToken cancellationToken);
    // The provider's own folders and labels (MailFolders.LabelPrefix / FolderPrefix keys).
    Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(MailAccount account, CancellationToken cancellationToken);
    // Puts the message in Drafts, replacing the draft replacesDraftId; returns the new draft's id.
    Task<string> SaveDraftAsync(MailAccount account, MimeMessage message, string? replacesDraftId, string? threadId, CancellationToken cancellationToken);
    Task DeleteDraftAsync(MailAccount account, string draftId, CancellationToken cancellationToken);
}

// Gmail through Google's API with the person's own Google sign-in (MailAccount.TokenProtected).
// An expired token, or a 401, is renewed with the refresh token and the app's Google client
// (GoogleOAuth settings), saved back on the account, and the call tried once more.
public sealed class GmailMailboxBackend(
    ISecretProtector secretProtector,
    IHttpClientFactory httpClientFactory,
    Microsoft.Extensions.Options.IOptions<GoogleOAuthOptions> googleOptions) : IMailboxBackend
{
    public const string HttpClientName = "Mail";
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const int PageSize = 25;

    public string Provider => MailProviders.Google;

    public Task<string?> VerifyAsync(MailAccount account, string password, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public async Task<MailPage> ListAsync(MailAccount account, string folder, string? search, string? pageToken, CancellationToken cancellationToken)
    {
        var labels = folder.StartsWith(MailFolders.LabelPrefix, StringComparison.Ordinal) ? folder[MailFolders.LabelPrefix.Length..] : folder switch
        {
            MailFolders.Inbox => "INBOX",
            MailFolders.Starred => "STARRED",
            MailFolders.Sent => "SENT",
            MailFolders.Drafts => "DRAFT",
            MailFolders.Spam => "SPAM",
            MailFolders.Trash => "TRASH",
            _ => null
        };
        // Archive: what's left the inbox, apart from sent mail, drafts, spam and trash.
        var folderQuery = labels is null ? "-in:inbox -in:spam -in:trash -in:drafts -in:sent" : null;
        var query = string.Join(' ', new[] { folderQuery, search }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var path = new StringBuilder($"messages?maxResults={PageSize}");
        if (labels is not null) path.Append("&labelIds=").Append(labels);
        if (query.Length > 0) path.Append("&q=").Append(Uri.EscapeDataString(query));
        if (folder is MailFolders.Spam or MailFolders.Trash) path.Append("&includeSpamTrash=true");
        if (!string.IsNullOrEmpty(pageToken)) path.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));

        var list = await CallAsync(account, HttpMethod.Get, path.ToString(), null, cancellationToken);
        var ids = list?["messages"]?.AsArray().Select(item => item?["id"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
        var next = list?["nextPageToken"]?.GetValue<string>();

        // One small request per message (Gmail's list gives only ids), a few at a time.
        using var gate = new SemaphoreSlim(8);
        var summaries = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var item = await CallAsync(account, HttpMethod.Get,
                    $"messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date&metadataHeaders=Content-Type",
                    null, cancellationToken);
                return item is null ? null : Summary(item);
            }
            finally
            {
                gate.Release();
            }
        }));
        return new MailPage(summaries.OfType<MailMessageSummary>().ToList(), string.IsNullOrEmpty(next) ? null : next);
    }

    public async Task<MailFetched> GetAsync(MailAccount account, string id, bool markRead, CancellationToken cancellationToken)
    {
        var item = await CallAsync(account, HttpMethod.Get, $"messages/{Uri.EscapeDataString(id)}?format=raw", null, cancellationToken)
            ?? throw new MailException("That message is gone.");
        var raw = item["raw"]?.GetValue<string>() ?? throw new MailException("Gmail sent the message back empty.");
        using var stream = new MemoryStream(FromBase64Url(raw));
        var message = await MimeMessage.LoadAsync(stream, cancellationToken);
        var labels = Labels(item);
        var unread = labels.Contains("UNREAD");
        if (markRead && unread) await ModifyAsync(account, id, [], ["UNREAD"], cancellationToken);
        return new MailFetched(message, item["threadId"]?.GetValue<string>(), unread && !markRead, labels.Contains("STARRED"));
    }

    public async Task SendAsync(MailAccount account, MimeMessage message, string? threadId, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        var body = new JsonObject { ["raw"] = ToBase64Url(stream.ToArray()) };
        if (!string.IsNullOrEmpty(threadId)) body["threadId"] = threadId;
        await CallAsync(account, HttpMethod.Post, "messages/send", body, cancellationToken);
    }

    public Task SetReadAsync(MailAccount account, string id, bool read, CancellationToken cancellationToken) =>
        read ? ModifyAsync(account, id, [], ["UNREAD"], cancellationToken) : ModifyAsync(account, id, ["UNREAD"], [], cancellationToken);

    public Task SetStarredAsync(MailAccount account, string id, bool starred, CancellationToken cancellationToken) =>
        starred ? ModifyAsync(account, id, ["STARRED"], [], cancellationToken) : ModifyAsync(account, id, [], ["STARRED"], cancellationToken);

    public Task ArchiveAsync(MailAccount account, string id, CancellationToken cancellationToken) =>
        ModifyAsync(account, id, [], ["INBOX"], cancellationToken);

    public async Task TrashAsync(MailAccount account, string id, CancellationToken cancellationToken) =>
        await CallAsync(account, HttpMethod.Post, $"messages/{Uri.EscapeDataString(id)}/trash", new JsonObject(), cancellationToken);

    public async Task<int> UnreadCountAsync(MailAccount account, CancellationToken cancellationToken) =>
        (await CallAsync(account, HttpMethod.Get, "labels/INBOX", null, cancellationToken))?["messagesUnread"]?.GetValue<int>() ?? 0;

    // The labels the person made (Gmail's own - Inbox, Sent, CATEGORY_* - are the standard folders).
    public async Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var labels = await CallAsync(account, HttpMethod.Get, "labels", null, cancellationToken);
        return labels?["labels"]?.AsArray()
            .Where(label => label?["type"]?.GetValue<string>() == "user")
            .Select(label => new MailFolderView(MailFolders.LabelPrefix + label!["id"]!.GetValue<string>(), label["name"]?.GetValue<string>() ?? "Label"))
            .OrderBy(label => label.Label, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    // Gmail drafts have their own ids; the list maps each to the message id Mail knows it by.
    public async Task<string> SaveDraftAsync(MailAccount account, MimeMessage message, string? replacesDraftId, string? threadId, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        var inner = new JsonObject { ["raw"] = ToBase64Url(stream.ToArray()) };
        if (!string.IsNullOrEmpty(threadId)) inner["threadId"] = threadId;
        var existing = replacesDraftId is null ? null : await DraftIdForAsync(account, replacesDraftId, cancellationToken);
        var saved = existing is null
            ? await CallAsync(account, HttpMethod.Post, "drafts", new JsonObject { ["message"] = inner }, cancellationToken)
            : await CallAsync(account, HttpMethod.Put, $"drafts/{Uri.EscapeDataString(existing)}", new JsonObject { ["id"] = existing, ["message"] = inner }, cancellationToken);
        return saved?["message"]?["id"]?.GetValue<string>() ?? throw new MailException("Gmail didn't say where it saved the draft.");
    }

    public async Task DeleteDraftAsync(MailAccount account, string draftId, CancellationToken cancellationToken)
    {
        if (await DraftIdForAsync(account, draftId, cancellationToken) is { } id)
            await CallAsync(account, HttpMethod.Delete, $"drafts/{Uri.EscapeDataString(id)}", null, cancellationToken);
    }

    private async Task<string?> DraftIdForAsync(MailAccount account, string messageId, CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var page = await CallAsync(account, HttpMethod.Get, "drafts?maxResults=500" + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)), null, cancellationToken);
            var match = page?["drafts"]?.AsArray().FirstOrDefault(draft => draft?["message"]?["id"]?.GetValue<string>() == messageId);
            if (match is not null) return match["id"]?.GetValue<string>();
            pageToken = page?["nextPageToken"]?.GetValue<string>();
        }
        while (pageToken is not null);
        return null;
    }

    private async Task ModifyAsync(MailAccount account, string id, string[] add, string[] remove, CancellationToken cancellationToken) =>
        await CallAsync(account, HttpMethod.Post, $"messages/{Uri.EscapeDataString(id)}/modify", new JsonObject
        {
            ["addLabelIds"] = new JsonArray(add.Select(label => (JsonNode)label).ToArray()),
            ["removeLabelIds"] = new JsonArray(remove.Select(label => (JsonNode)label).ToArray())
        }, cancellationToken);

    private static MailMessageSummary Summary(JsonNode item)
    {
        var headers = item["payload"]?["headers"]?.AsArray()
            .Select(header => (Name: header?["name"]?.GetValue<string>() ?? string.Empty, Value: header?["value"]?.GetValue<string>() ?? string.Empty))
            .ToList() ?? [];
        string Header(string name) => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
        var labels = Labels(item);
        var date = long.TryParse(item["internalDate"]?.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.MinValue;
        return new MailMessageSummary(
            item["id"]!.GetValue<string>(), item["threadId"]?.GetValue<string>(),
            ParseAddresses(Header("From")).FirstOrDefault() ?? new MailAddress(string.Empty, string.Empty), ParseAddresses(Header("To")),
            Header("Subject"), WebUtility.HtmlDecode(item["snippet"]?.GetValue<string>() ?? string.Empty), date,
            labels.Contains("UNREAD"), labels.Contains("STARRED"),
            Header("Content-Type").StartsWith("multipart/mixed", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> Labels(JsonNode item) =>
        item["labelIds"]?.AsArray().Select(label => label?.GetValue<string>()).OfType<string>().ToHashSet() ?? [];

    internal static IReadOnlyList<MailAddress> ParseAddresses(string header) =>
        InternetAddressList.TryParse(header, out var list) ? MailMessageRenderer.Addresses(list) : [];

    private async Task<JsonNode?> CallAsync(MailAccount account, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        var tokens = Tokens(account);
        if (DateTimeOffset.TryParse(tokens["expiresAt"]?.GetValue<string>(), out var expiresAt) && expiresAt < DateTimeOffset.UtcNow.AddMinutes(1))
            tokens = await RefreshAsync(account, tokens, cancellationToken);
        var response = await SendOnceAsync(tokens, method, path, body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            tokens = await RefreshAsync(account, tokens, cancellationToken);
            response = await SendOnceAsync(tokens, method, path, body, cancellationToken);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);
            var reason = Truncate(JsonNode.Parse(text.Length == 0 ? "{}" : text)?["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase ?? string.Empty, 200);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new MailException("Google signed this mailbox out - connect Google again on the Mail page."),
                HttpStatusCode.Forbidden => new MailException($"Google refused ({reason}). Connect Google again on the Mail page and allow Gmail."),
                HttpStatusCode.NotFound => new MailException("That message is gone."),
                _ => new MailException($"Gmail said {(int)response.StatusCode}: {reason}")
            };
        }
    }

    private JsonObject Tokens(MailAccount account) =>
        account.TokenProtected.Length > 0 && JsonNode.Parse(secretProtector.Unprotect(account.TokenProtected)) is JsonObject tokens
            ? tokens
            : throw new MailException("Connect Google again on the Mail page.");

    // A new access token from the refresh token, kept on the account (MailService saves it).
    private async Task<JsonObject> RefreshAsync(MailAccount account, JsonObject tokens, CancellationToken cancellationToken)
    {
        var refreshToken = tokens["refreshToken"]?.GetValue<string>();
        var options = googleOptions.Value;
        if (string.IsNullOrWhiteSpace(refreshToken) || !options.IsConfigured)
            throw new MailException("Google signed this mailbox out - connect Google again on the Mail page.");
        using var response = await httpClientFactory.CreateClient(HttpClientName).PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        }), cancellationToken);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        var accessToken = answer?["access_token"]?.GetValue<string>();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(accessToken))
            throw new MailException("Google signed this mailbox out - connect Google again on the Mail page.");
        tokens["accessToken"] = accessToken;
        if (answer!["refresh_token"]?.GetValue<string>() is { Length: > 0 } newRefresh) tokens["refreshToken"] = newRefresh;
        if (answer["expires_in"] is JsonValue seconds && seconds.TryGetValue<int>(out var lifetime))
            tokens["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(lifetime).ToString("O");
        account.TokenProtected = secretProtector.Protect(tokens.ToJsonString());
        return tokens;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(JsonObject tokens, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        var token = tokens["accessToken"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token)) throw new MailException("Connect Google again on the Mail page.");
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
    }

    public static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    public static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

// An IMAP message id: the folder, its UIDVALIDITY (so a rebuilt folder can't point at the wrong
// message) and the UID, in one URL-safe string.
public static class ImapMessageId
{
    public static string Encode(string folder, uint uidValidity, uint uid) =>
        GmailMailboxBackend.ToBase64Url(Encoding.UTF8.GetBytes($"{uidValidity}:{uid}:{folder}"));

    public static (string Folder, uint UidValidity, uint Uid) Decode(string id)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(GmailMailboxBackend.FromBase64Url(id)).Split(':', 3);
            return (parts[2], uint.Parse(parts[0], CultureInfo.InvariantCulture), uint.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new MailException("That message link isn't valid.");
        }
    }
}

// Any other provider: IMAP to read (TLS on 993), SMTP to send, with an app password. Each call
// signs in, does its work and signs out.
public sealed class ImapMailboxBackend(ISecretProtector secretProtector) : IMailboxBackend
{
    private const int PageSize = 25;
    private static readonly Dictionary<string, (SpecialFolder Special, string[] Names)> Specials = new()
    {
        [MailFolders.Sent] = (SpecialFolder.Sent, ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "[Gmail]/Sent Mail"]),
        [MailFolders.Drafts] = (SpecialFolder.Drafts, ["Drafts", "[Gmail]/Drafts"]),
        [MailFolders.Archive] = (SpecialFolder.Archive, ["Archive", "Archives", "[Gmail]/All Mail"]),
        [MailFolders.Spam] = (SpecialFolder.Junk, ["Junk", "Junk E-mail", "Junk Email", "Spam", "Bulk Mail", "[Gmail]/Spam"]),
        [MailFolders.Trash] = (SpecialFolder.Trash, ["Trash", "Deleted Items", "Deleted Messages", "Bin", "[Gmail]/Trash", "[Gmail]/Bin"])
    };

    public string Provider => MailProviders.Imap;

    public async Task<string?> VerifyAsync(MailAccount account, string password, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await ImapMailboxConnector.ConnectAsync(account.ImapHost, account.ImapPort, account.LoginUsername, password, cancellationToken);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailException)
        {
            throw new MailException($"Couldn't sign in to {account.ImapHost} (IMAP): {ex.Message}");
        }
        using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 15_000 };
        try
        {
            await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, MailTransport.ParseSecurity(account.SmtpSecurity), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = await SmtpReachability.DescribeConnectFailureAsync(account.SmtpHost, account.SmtpPort, ex, cancellationToken);
            if (failure.PortBlocked) return $"Reading works, so the app password is right, but mail can't be sent from here yet. {failure.Message}";
            throw new MailException($"Signed in to read mail, but couldn't connect to {account.SmtpHost} to send (SMTP): {failure.Message}");
        }
        try
        {
            await smtp.AuthenticateAsync(account.LoginUsername, password, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MailException($"Signed in to read mail, but couldn't sign in to {account.SmtpHost} to send (SMTP): {ex.Message}");
        }
        return null;
    }

    public async Task<MailPage> ListAsync(MailAccount account, string folder, string? search, string? pageToken, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var mailFolder = await FolderAsync(client, folder, cancellationToken)
            ?? throw new MailException($"This mailbox has no {folder} folder.");
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        SearchQuery query = folder == MailFolders.Starred ? SearchQuery.Flagged : SearchQuery.All;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.And(SearchQuery.SubjectContains(text).Or(SearchQuery.FromContains(text)).Or(SearchQuery.BodyContains(text)));
        }
        var uids = (await mailFolder.SearchAsync(query, cancellationToken)).OrderByDescending(uid => uid.Id).ToList();
        var offset = int.TryParse(pageToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? Math.Max(0, start) : 0;
        var page = uids.Skip(offset).Take(PageSize).ToList();
        var fetched = page.Count == 0 ? [] : await mailFolder.FetchAsync(page,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
            | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText, cancellationToken);
        var summaries = fetched.OrderByDescending(item => item.UniqueId.Id).Select(item => new MailMessageSummary(
            ImapMessageId.Encode(mailFolder.FullName, mailFolder.UidValidity, item.UniqueId.Id), null,
            MailMessageRenderer.Address(item.Envelope?.From.Mailboxes.FirstOrDefault()),
            item.Envelope is null ? [] : MailMessageRenderer.Addresses(item.Envelope.To),
            item.Envelope?.Subject ?? string.Empty, item.PreviewText ?? string.Empty,
            item.Envelope?.Date ?? item.InternalDate ?? DateTimeOffset.MinValue,
            item.Flags is { } flags && !flags.HasFlag(MessageFlags.Seen), item.Flags?.HasFlag(MessageFlags.Flagged) == true,
            item.Attachments?.Any() == true)).ToList();
        await client.DisconnectAsync(true, cancellationToken);
        return new MailPage(summaries, offset + PageSize < uids.Count ? (offset + PageSize).ToString(CultureInfo.InvariantCulture) : null);
    }

    public async Task<MailFetched> GetAsync(MailAccount account, string id, bool markRead, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var (folder, uid) = await OpenAsync(client, id, markRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly, cancellationToken);
        var message = await folder.GetMessageAsync(uid, cancellationToken);
        var flags = (await folder.FetchAsync([uid], MessageSummaryItems.Flags, cancellationToken)).FirstOrDefault()?.Flags ?? MessageFlags.None;
        var unread = !flags.HasFlag(MessageFlags.Seen);
        if (markRead && unread) await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return new MailFetched(message, null, unread && !markRead, flags.HasFlag(MessageFlags.Flagged));
    }

    public async Task SendAsync(MailAccount account, MimeMessage message, string? threadId, CancellationToken cancellationToken)
    {
        var password = Password(account);
        using (var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 30_000 })
        {
            try
            {
                await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, MailTransport.ParseSecurity(account.SmtpSecurity), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = await SmtpReachability.DescribeConnectFailureAsync(account.SmtpHost, account.SmtpPort, ex, cancellationToken);
                throw new MailException(failure.PortBlocked
                    ? $"Couldn't send. {failure.Message} {SmtpReachability.PasswordNeverTried}"
                    : $"Couldn't connect to {account.SmtpHost} to send: {failure.Message}");
            }
            await smtp.AuthenticateAsync(account.LoginUsername, password, cancellationToken);
            await smtp.SendAsync(message, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        // Gmail files what it sends itself; other servers need the copy put in Sent.
        if (IsGmail(account)) return;
        try
        {
            using var client = await ConnectAsync(account, cancellationToken);
            if (await FolderAsync(client, MailFolders.Sent, cancellationToken) is { } sent)
            {
                await sent.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
                await sent.AppendAsync(new AppendRequest(message, MessageFlags.Seen), cancellationToken);
            }
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MailException($"Sent, but couldn't put a copy in Sent: {ex.Message}");
        }
    }

    public Task SetReadAsync(MailAccount account, string id, bool read, CancellationToken cancellationToken) =>
        FlagAsync(account, id, MessageFlags.Seen, read, cancellationToken);

    public Task SetStarredAsync(MailAccount account, string id, bool starred, CancellationToken cancellationToken) =>
        FlagAsync(account, id, MessageFlags.Flagged, starred, cancellationToken);

    public Task ArchiveAsync(MailAccount account, string id, CancellationToken cancellationToken) =>
        MoveAsync(account, id, MailFolders.Archive, cancellationToken);

    public Task TrashAsync(MailAccount account, string id, CancellationToken cancellationToken) =>
        MoveAsync(account, id, MailFolders.Trash, cancellationToken);

    public async Task<int> UnreadCountAsync(MailAccount account, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        await client.Inbox.StatusAsync(StatusItems.Unread, cancellationToken);
        var unread = client.Inbox.Unread;
        await client.DisconnectAsync(true, cancellationToken);
        return unread;
    }

    // Every folder that isn't the inbox or one of the standard ones, by its full path.
    public async Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(MailAccount account, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { client.Inbox.FullName };
        foreach (var key in Specials.Keys)
            if (await FolderAsync(client, key, cancellationToken) is { } folder) standard.Add(folder.FullName);
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
            foreach (var extra in new[] { SpecialFolder.All, SpecialFolder.Flagged, SpecialFolder.Important })
                if (client.GetFolder(extra) is { } folder) standard.Add(folder.FullName);
        var folders = client.PersonalNamespaces.Count == 0 ? [] : await client.GetFoldersAsync(client.PersonalNamespaces[0], false, cancellationToken);
        var custom = folders
            .Where(folder => !standard.Contains(folder.FullName) && !folder.Attributes.HasFlag(FolderAttributes.NoSelect) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent))
            .Select(folder => new MailFolderView(MailFolders.FolderPrefix + folder.FullName, folder.FullName.Replace(folder.DirectorySeparator.ToString(), " / ")))
            .OrderBy(folder => folder.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        await client.DisconnectAsync(true, cancellationToken);
        return custom;
    }

    public async Task<string> SaveDraftAsync(MailAccount account, MimeMessage message, string? replacesDraftId, string? threadId, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var drafts = await FolderAsync(client, MailFolders.Drafts, cancellationToken) ?? throw new MailException("This mailbox has no Drafts folder.");
        await drafts.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var uid = await drafts.AppendAsync(new AppendRequest(message, MessageFlags.Draft | MessageFlags.Seen), cancellationToken);
        var id = uid is { } saved ? ImapMessageId.Encode(drafts.FullName, drafts.UidValidity, saved.Id)
            : throw new MailException("The server saved the draft but didn't say where - look in Drafts.");
        await client.DisconnectAsync(true, cancellationToken);
        if (replacesDraftId is not null) await DeleteDraftAsync(account, replacesDraftId, cancellationToken);
        return id;
    }

    public async Task DeleteDraftAsync(MailAccount account, string draftId, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var (folder, uid) = await OpenAsync(client, draftId, FolderAccess.ReadWrite, cancellationToken);
        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await folder.ExpungeAsync([uid], cancellationToken);
        else await folder.ExpungeAsync(cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task FlagAsync(MailAccount account, string id, MessageFlags flag, bool on, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var (folder, uid) = await OpenAsync(client, id, FolderAccess.ReadWrite, cancellationToken);
        if (on) await folder.AddFlagsAsync(uid, flag, true, cancellationToken);
        else await folder.RemoveFlagsAsync(uid, flag, true, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task MoveAsync(MailAccount account, string id, string target, CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        var (folder, uid) = await OpenAsync(client, id, FolderAccess.ReadWrite, cancellationToken);
        var destination = await FolderAsync(client, target, cancellationToken);
        if (destination is not null && destination.FullName != folder.FullName)
        {
            await folder.MoveToAsync(uid, destination, cancellationToken);
        }
        else if (target == MailFolders.Trash)
        {
            // Already in Trash (or there's none): delete it for good.
            await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
            if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await folder.ExpungeAsync([uid], cancellationToken);
            else await folder.ExpungeAsync(cancellationToken);
        }
        else
        {
            throw new MailException("This mailbox has no Archive folder.");
        }
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task<(IMailFolder Folder, UniqueId Uid)> OpenAsync(ImapClient client, string id, FolderAccess access, CancellationToken cancellationToken)
    {
        var (path, uidValidity, uid) = ImapMessageId.Decode(id);
        var folder = await client.GetFolderAsync(path, cancellationToken);
        await folder.OpenAsync(access, cancellationToken);
        if (folder.UidValidity != uidValidity) throw new MailException("That folder changed on the server - reload the list.");
        return (folder, new UniqueId(uidValidity, uid));
    }

    private static async Task<IMailFolder?> FolderAsync(ImapClient client, string key, CancellationToken cancellationToken)
    {
        if (key is MailFolders.Inbox or MailFolders.Starred) return client.Inbox;
        if (key.StartsWith(MailFolders.FolderPrefix, StringComparison.Ordinal))
            return await client.GetFolderAsync(key[MailFolders.FolderPrefix.Length..], cancellationToken);
        if (!Specials.TryGetValue(key, out var special)) return null;
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            if (client.GetFolder(special.Special) is { } found) return found;
            // Gmail calls its archive All Mail.
            if (key == MailFolders.Archive && client.GetFolder(SpecialFolder.All) is { } all) return all;
        }
        if (client.PersonalNamespaces.Count == 0) return null;
        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], false, cancellationToken);
        return special.Names
            .Select(name => folders.FirstOrDefault(folder => string.Equals(folder.FullName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(folder => folder is not null);
    }

    private async Task<ImapClient> ConnectAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            return await ImapMailboxConnector.ConnectAsync(account.ImapHost, account.ImapPort, account.LoginUsername, Password(account), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailException)
        {
            throw new MailException($"Couldn't sign in to {account.ImapHost}: {ex.Message}");
        }
    }

    private string Password(MailAccount account) =>
        account.PasswordProtected.Length > 0 ? secretProtector.Unprotect(account.PasswordProtected) : throw new MailException("Enter the mailbox's app password again.");

    private static bool IsGmail(MailAccount account) =>
        account.ImapHost.Equals("imap.gmail.com", StringComparison.OrdinalIgnoreCase) || account.SmtpHost.Equals("smtp.gmail.com", StringComparison.OrdinalIgnoreCase);
}
