using System.Text.Json;
using GwsBusinessSuite.Application.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class GoogleOAuthOptions
{
    public const string SectionName = "GoogleOAuth";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

public sealed class GoogleOAuthService(
    HttpClient httpClient,
    IOptions<GoogleOAuthOptions> configuredOptions,
    IAutomationCredentialService credentialService,
    ILogger<GoogleOAuthService> logger) : IGoogleOAuthService
{
    public const string GmailModifyScope = "https://www.googleapis.com/auth/gmail.modify";
    // Full Gmail access: the only scope Gmail's IMAP accepts (XOAUTH2), so mail-in can read the
    // plus-addresses through this connection instead of an app password.
    public const string FullMailScope = "https://mail.google.com/";

    // Requested together so one "Connect Google" click yields a single credential usable by the
    // gmail.sendEmail and calendar.createEvent nodes, and (31-10-26) by Mail and mail-in, which
    // read, mark read, archive and trash. gmail.send stays so a send-only consent still sends.
    private const string Scopes = "https://www.googleapis.com/auth/gmail.send " + FullMailScope + " https://www.googleapis.com/auth/calendar.events";

    public static bool CanReadMail(string? grantedScopes) =>
        (grantedScopes ?? string.Empty).Split(' ') is var granted && (granted.Contains(FullMailScope) || granted.Contains(GmailModifyScope));

    public static bool CanUseImap(string? grantedScopes) => (grantedScopes ?? string.Empty).Split(' ').Contains(FullMailScope);
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ProfileEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/profile";
    private readonly GoogleOAuthOptions _options = configuredOptions.Value;

    public bool IsConfigured => _options.IsConfigured;

    public string CreateAuthorizationUrl(string state)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Google OAuth is not configured. Set GoogleOAuth:ClientId, ClientSecret, and RedirectUri.");
        }

        return "https://accounts.google.com/o/oauth2/v2/auth"
            + $"?client_id={Uri.EscapeDataString(_options.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}"
            + "&response_type=code"
            // offline + consent forces Google to actually return a refresh_token - by default
            // it only does that on a user's very first authorization.
            + "&access_type=offline"
            + "&prompt=consent"
            + $"&scope={Uri.EscapeDataString(Scopes)}"
            + $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<AutomationOAuthConnectionResult> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken = default)
    {
        var (tokens, error) = await ExchangeAsync(code, cancellationToken);
        if (tokens is null) return new AutomationOAuthConnectionResult(false, error!);

        var canRead = tokens.CanReadMail;
        var what = canRead ? "Gmail read + send, Calendar events" : "Gmail send + Calendar events";
        await SaveAsync("Google", tokens, $"Connected via Google OAuth{(tokens.Address is null ? "" : $" as {tokens.Address}")} ({what}).", cancellationToken);
        return new AutomationOAuthConnectionResult(true, $"Connected to Google ({what}).");
    }

    public async Task<GoogleMailConnectionResult> CompleteMailAuthorizationAsync(string code, CancellationToken cancellationToken = default)
    {
        var (tokens, error) = await ExchangeAsync(code, cancellationToken);
        if (tokens is null) return new GoogleMailConnectionResult(false, error!);
        if (!tokens.CanReadMail)
            return new GoogleMailConnectionResult(false, "Google didn't allow reading mail - connect again and tick the Gmail box on Google's screen.");
        if (tokens.Address is null)
            return new GoogleMailConnectionResult(false, "Google didn't say which mailbox this is - try connecting again.");
        // The client id and secret stay in configuration; refreshing reads them from there.
        var tokenJson = JsonSerializer.Serialize(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            expiresAt = tokens.ExpiresAt?.ToString("O"),
            scope = tokens.Scope,
            email = tokens.Address
        });
        return new GoogleMailConnectionResult(true, $"Connected {tokens.Address}.", tokens.Address, tokenJson);
    }

    private sealed record Tokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string Scope, string? Address)
    {
        public bool CanReadMail => GoogleOAuthService.CanReadMail(Scope);
    }

    private async Task<(Tokens? Tokens, string? Error)> ExchangeAsync(string code, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return (null, "Google OAuth is not configured.");
        if (string.IsNullOrWhiteSpace(code)) return (null, "Google did not return an authorization code.");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code.Trim(),
            ["redirect_uri"] = _options.RedirectUri,
            ["grant_type"] = "authorization_code"
        });

        using var response = await httpClient.PostAsync(TokenEndpoint, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google OAuth token exchange failed with status {StatusCode}: {Body}", (int)response.StatusCode, body);
            return (null, "Google authorization failed.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken)) return (null, "Google did not return an access token.");
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            logger.LogWarning("Google OAuth did not return a refresh token - the connection will stop working once the access token expires. Disconnect any prior Google connection in your Google Account settings and try again.");
        }
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
        // Google lets the person untick scopes on its screen, so what was granted is checked.
        var scope = root.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() ?? string.Empty : string.Empty;
        var tokens = new Tokens(accessToken, refreshToken, expiresAt, scope, null);
        return (tokens.CanReadMail ? tokens with { Address = await GetAddressAsync(accessToken, cancellationToken) } : tokens, null);
    }

    // Which mailbox this is (needs a Gmail read scope).
    private async Task<string?> GetAddressAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProfileEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("emailAddress", out var address) ? address.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Couldn't read the connected Google account's address.");
            return null;
        }
    }

    // Reconnecting updates the credential with this name rather than adding a second one (the
    // first "Google" found is the one mail goes out through).
    private async Task<Guid> SaveAsync(string name, Tokens tokens, string description, CancellationToken cancellationToken)
    {
        var credentialJson = JsonSerializer.Serialize(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            tokenEndpoint = TokenEndpoint,
            clientId = _options.ClientId,
            clientSecret = _options.ClientSecret,
            expiresAt = tokens.ExpiresAt?.ToString("O"),
            scope = tokens.Scope,
            email = tokens.Address
        });
        var existing = (await credentialService.ListAsync(cancellationToken)).FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.TypeKey, AutomationCredentialService.OAuth2TypeKey, StringComparison.OrdinalIgnoreCase));
        return await credentialService.SaveAsync(existing?.Id, name, AutomationCredentialService.OAuth2TypeKey, credentialJson, description, cancellationToken);
    }
}
