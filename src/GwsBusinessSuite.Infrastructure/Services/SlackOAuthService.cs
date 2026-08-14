using System.Text.Json;
using GwsBusinessSuite.Application.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as NotionOAuthOptions - a dedicated OAuth app registration per connector,
// matching this codebase's existing convention rather than a shared multi-provider config
// blob. The user must register a Slack app and supply these before this connector works.
public sealed class SlackOAuthOptions
{
    public const string SectionName = "SlackOAuth";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

public sealed class SlackOAuthService(
    HttpClient httpClient,
    IOptions<SlackOAuthOptions> configuredOptions,
    IAutomationCredentialService credentialService,
    ILogger<SlackOAuthService> logger) : ISlackOAuthService
{
    private readonly SlackOAuthOptions _options = configuredOptions.Value;

    public bool IsConfigured => _options.IsConfigured;

    public string CreateAuthorizationUrl(string state)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Slack OAuth is not configured. Set SlackOAuth:ClientId, ClientSecret, and RedirectUri.");
        }

        return "https://slack.com/oauth/v2/authorize"
            + $"?client_id={Uri.EscapeDataString(_options.ClientId)}"
            // chat:write is all the automation node actually needs (send a message as the
            // connected app/bot) - kept to the narrowest scope the feature requires.
            + "&scope=chat:write"
            + $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<AutomationOAuthConnectionResult> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new AutomationOAuthConnectionResult(false, "Slack OAuth is not configured.");
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return new AutomationOAuthConnectionResult(false, "Slack did not return an authorization code.");
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code.Trim(),
            ["redirect_uri"] = _options.RedirectUri
        });

        using var response = await httpClient.PostAsync("https://slack.com/api/oauth.v2.access", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        if (!response.IsSuccessStatusCode || !ok)
        {
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : "unknown_error";
            logger.LogWarning("Slack OAuth token exchange failed: {Error}", error);
            return new AutomationOAuthConnectionResult(false, $"Slack authorization failed: {error}.");
        }

        var accessToken = root.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new AutomationOAuthConnectionResult(false, "Slack did not return an access token.");
        }
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
        var teamName = root.TryGetProperty("team", out var teamElement)
            && teamElement.TryGetProperty("name", out var teamNameElement)
                ? teamNameElement.GetString()
                : "Slack";

        var credentialJson = JsonSerializer.Serialize(new
        {
            accessToken,
            refreshToken,
            // Slack's own token-rotation exchange reuses this same endpoint with
            // grant_type=refresh_token - see IAutomationCredentialService.RefreshOAuthCredentialAsync.
            tokenEndpoint = "https://slack.com/api/oauth.v2.access",
            clientId = _options.ClientId,
            clientSecret = _options.ClientSecret
        });
        await credentialService.SaveAsync(
            null, $"Slack ({teamName})", AutomationCredentialService.OAuth2TypeKey, credentialJson,
            "Connected via Slack OAuth.", cancellationToken);

        return new AutomationOAuthConnectionResult(true, $"Connected to the Slack workspace \"{teamName}\".");
    }
}
