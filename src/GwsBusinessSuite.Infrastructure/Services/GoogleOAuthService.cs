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
    // Requested together so one "Connect Google" click yields a single credential usable by
    // both gmail.sendEmail and calendar.createEvent nodes.
    private const string Scopes = "https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/calendar.events";
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
        if (!IsConfigured)
        {
            return new AutomationOAuthConnectionResult(false, "Google OAuth is not configured.");
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return new AutomationOAuthConnectionResult(false, "Google did not return an authorization code.");
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code.Trim(),
            ["redirect_uri"] = _options.RedirectUri,
            ["grant_type"] = "authorization_code"
        });

        using var response = await httpClient.PostAsync("https://oauth2.googleapis.com/token", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google OAuth token exchange failed with status {StatusCode}: {Body}", (int)response.StatusCode, body);
            return new AutomationOAuthConnectionResult(false, "Google authorization failed.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new AutomationOAuthConnectionResult(false, "Google did not return an access token.");
        }
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            logger.LogWarning("Google OAuth did not return a refresh token - the connection will stop working once the access token expires. Disconnect any prior Google connection in your Google Account settings and try again.");
        }
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;

        var credentialJson = JsonSerializer.Serialize(new
        {
            accessToken,
            refreshToken,
            tokenEndpoint = "https://oauth2.googleapis.com/token",
            clientId = _options.ClientId,
            clientSecret = _options.ClientSecret,
            expiresAt = expiresAt?.ToString("O")
        });
        await credentialService.SaveAsync(
            null, "Google", AutomationCredentialService.OAuth2TypeKey, credentialJson,
            "Connected via Google OAuth (Gmail send + Calendar events).", cancellationToken);

        return new AutomationOAuthConnectionResult(true, "Connected to Google (Gmail send + Calendar events).");
    }
}
