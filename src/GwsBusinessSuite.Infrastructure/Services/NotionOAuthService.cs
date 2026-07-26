using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NotionOAuthService(
    HttpClient httpClient,
    IOptions<NotionOAuthOptions> options,
    IAppDbContext dbContext,
    ISecretProtector secretProtector,
    ILogger<NotionOAuthService> logger) : INotionOAuthService
{
    private readonly NotionOAuthOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public string CreateAuthorizationUrl(string state)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Notion OAuth is not configured. Set NotionOAuth:ClientId, ClientSecret, and RedirectUri.");
        }

        return "https://api.notion.com/v1/oauth/authorize"
            + $"?owner=user&client_id={Uri.EscapeDataString(_options.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}"
            + "&response_type=code"
            + $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<NotionOAuthConnectionResult> CompleteAuthorizationAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return NotConfigured();
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return new NotionOAuthConnectionResult(false, "Notion did not return an authorization code.");
        }

        var exchange = await ExchangeTokenAsync(
            new
            {
                grant_type = "authorization_code",
                code = code.Trim(),
                redirect_uri = _options.RedirectUri
            },
            cancellationToken);
        if (!exchange.IsSuccess || exchange.Token is null)
        {
            return new NotionOAuthConnectionResult(false, exchange.Message);
        }

        await StoreOAuthConnectionAsync(
            exchange.Token,
            preserveExistingRefreshToken: false,
            cancellationToken);
        return new NotionOAuthConnectionResult(
            true,
            "Notion workspace connected.",
            exchange.Token.WorkspaceName);
    }

    public async Task<NotionOAuthConnectionResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return NotConfigured();
        }

        var settings = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null
            || !string.Equals(settings.AuthenticationMode, "oauth", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.OAuthRefreshToken))
        {
            return new NotionOAuthConnectionResult(false, "No refreshable Notion OAuth connection is stored.");
        }

        string refreshToken;
        try
        {
            refreshToken = secretProtector.Unprotect(settings.OAuthRefreshToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stored Notion OAuth refresh token could not be decrypted.");
            return new NotionOAuthConnectionResult(false, "The stored Notion OAuth token is unreadable. Connect again.");
        }

        var exchange = await ExchangeTokenAsync(
            new { grant_type = "refresh_token", refresh_token = refreshToken },
            cancellationToken);
        if (!exchange.IsSuccess || exchange.Token is null)
        {
            return new NotionOAuthConnectionResult(false, exchange.Message);
        }

        await StoreOAuthConnectionAsync(
            exchange.Token,
            preserveExistingRefreshToken: true,
            cancellationToken);
        return new NotionOAuthConnectionResult(
            true,
            "Notion authorization refreshed.",
            exchange.Token.WorkspaceName);
    }

    public async Task<NotionOAuthConnectionResult> DisconnectAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.IntegrationToken))
        {
            return new NotionOAuthConnectionResult(true, "Notion is already disconnected.");
        }
        if (!string.Equals(settings.AuthenticationMode, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            return new NotionOAuthConnectionResult(
                false,
                "This connector uses a manually entered token. Clear or replace it from the connector settings.");
        }
        if (!IsConfigured)
        {
            return NotConfigured();
        }

        string accessToken;
        try
        {
            accessToken = secretProtector.Unprotect(settings.IntegrationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stored Notion OAuth access token could not be decrypted during disconnect.");
            return new NotionOAuthConnectionResult(false, "The stored Notion OAuth token is unreadable.");
        }

        using var request = CreateOAuthRequest(
            "oauth/revoke",
            JsonSerializer.Serialize(new { token = accessToken }));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Notion OAuth revoke request failed.");
            return new NotionOAuthConnectionResult(
                false,
                "Notion could not be reached to revoke the connection. Nothing was cleared locally.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new NotionOAuthConnectionResult(
                    false,
                    await ReadOAuthErrorAsync(response, cancellationToken));
            }
        }

        settings.IntegrationToken = string.Empty;
        settings.OAuthRefreshToken = string.Empty;
        settings.AuthenticationMode = "internal";
        settings.OAuthBotId = null;
        settings.WorkspaceId = null;
        settings.WorkspaceIconUrl = null;
        settings.OAuthConnectedAt = null;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedBy = "notion-oauth";
        await dbContext.SaveChangesAsync(cancellationToken);

        return new NotionOAuthConnectionResult(true, "Notion workspace disconnected.");
    }

    private async Task StoreOAuthConnectionAsync(
        NotionOAuthToken token,
        bool preserveExistingRefreshToken,
        CancellationToken cancellationToken)
    {
        var settings = await dbContext.NotionConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new NotionConnectorSettings { Id = NotionConnectorSettings.WellKnownId };
            dbContext.NotionConnectorSettings.Add(settings);
        }

        settings.IntegrationToken = secretProtector.Protect(token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            settings.OAuthRefreshToken = secretProtector.Protect(token.RefreshToken);
        }
        else if (!preserveExistingRefreshToken)
        {
            settings.OAuthRefreshToken = string.Empty;
        }
        settings.AuthenticationMode = "oauth";
        settings.OAuthBotId = token.BotId;
        settings.WorkspaceId = token.WorkspaceId;
        settings.WorkspaceName = token.WorkspaceName;
        settings.WorkspaceIconUrl = token.WorkspaceIconUrl;
        settings.OAuthConnectedAt = DateTimeOffset.UtcNow;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedBy = "notion-oauth";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<OAuthExchangeResult> ExchangeTokenAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateOAuthRequest("oauth/token", JsonSerializer.Serialize(payload));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Notion OAuth token request failed.");
            return new OAuthExchangeResult(
                false,
                "Notion could not be reached to complete authorization.",
                null);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new OAuthExchangeResult(
                    false,
                    await ReadOAuthErrorAsync(response, cancellationToken),
                    null);
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var accessToken = ReadString(root, "access_token");
            var botId = ReadString(root, "bot_id");
            var workspaceId = ReadString(root, "workspace_id");
            if (accessToken is null || botId is null || workspaceId is null)
            {
                return new OAuthExchangeResult(
                    false,
                    "Notion returned an incomplete OAuth token response.",
                    null);
            }

            return new OAuthExchangeResult(
                true,
                "Connected.",
                new NotionOAuthToken(
                    accessToken,
                    ReadString(root, "refresh_token"),
                    botId,
                    workspaceId,
                    ReadString(root, "workspace_name"),
                    ReadString(root, "workspace_icon")));
        }
    }

    private HttpRequestMessage CreateOAuthRequest(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Notion-Version", NotionService.NotionVersion);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<string> ReadOAuthErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var description = ReadString(root, "error_description")
                ?? ReadString(root, "message")
                ?? ReadString(root, "error");
            if (!string.IsNullOrWhiteSpace(description))
            {
                return $"Notion authorization failed: {description}";
            }
        }
        catch (JsonException)
        {
            // Use the bounded status-only fallback below.
        }

        return $"Notion authorization failed with status {(int)response.StatusCode}.";
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static NotionOAuthConnectionResult NotConfigured() =>
        new(
            false,
            "Notion OAuth is not configured. Set the client ID, client secret, and redirect URI.");

    private sealed record NotionOAuthToken(
        string AccessToken,
        string? RefreshToken,
        string BotId,
        string WorkspaceId,
        string? WorkspaceName,
        string? WorkspaceIconUrl);

    private sealed record OAuthExchangeResult(
        bool IsSuccess,
        string Message,
        NotionOAuthToken? Token);
}
