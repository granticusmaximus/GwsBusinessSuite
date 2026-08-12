using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationCredentialService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    TimeProvider timeProvider,
    // Optional, same pattern as SentinelAiService's wikiDatabaseService/accessService - only
    // RefreshOAuthCredentialAsync/RefreshExpiringOAuthCredentialsAsync need it, and every
    // existing call site (production DI and tests) predates OAuth2 refresh and passes just the
    // first three arguments.
    IAutomationHttpClient? httpClient = null) : IAutomationCredentialService
{
    public const string OAuth2TypeKey = "oauth2";

    public async Task<IReadOnlyList<AutomationCredentialSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.AutomationCredentials.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new AutomationCredentialSummary(
                item.Id, item.Name, item.TypeKey, item.Description, item.LastUsedAt, item.UpdatedAt ?? item.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<Guid> SaveAsync(
        Guid? id,
        string name,
        string typeKey,
        string credentialJson,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Credential name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(typeKey)) throw new ArgumentException("Credential type is required.", nameof(typeKey));
        try { System.Text.Json.JsonDocument.Parse(credentialJson).Dispose(); }
        catch (System.Text.Json.JsonException ex) { throw new ArgumentException($"Credential data must be valid JSON: {ex.Message}", nameof(credentialJson)); }

        AutomationCredential credential;
        if (id.HasValue)
        {
            credential = await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == id.Value, cancellationToken)
                ?? throw new KeyNotFoundException("Credential was not found.");
        }
        else
        {
            credential = new AutomationCredential { Name = name.Trim(), TypeKey = typeKey.Trim(), CreatedBy = "user" };
            db.AutomationCredentials.Add(credential);
        }

        credential.Name = name.Trim();
        credential.TypeKey = typeKey.Trim();
        credential.ProtectedData = secretProtector.Protect(credentialJson);
        credential.Description = description?.Trim() ?? string.Empty;
        credential.UpdatedAt = timeProvider.GetUtcNow();
        credential.UpdatedBy = "user";
        await db.SaveChangesAsync(cancellationToken);
        return credential.Id;
    }

    public async Task<string?> GetDecryptedDataAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == credentialId, cancellationToken);
        if (credential is null) return null;
        credential.LastUsedAt = timeProvider.GetUtcNow();
        credential.UpdatedAt = credential.LastUsedAt;
        credential.UpdatedBy = "automation-engine";
        await db.SaveChangesAsync(cancellationToken);
        return secretProtector.Unprotect(credential.ProtectedData);
    }

    public async Task<bool> RefreshOAuthCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await db.AutomationCredentials.FirstOrDefaultAsync(item => item.Id == credentialId, cancellationToken)
            ?? throw new KeyNotFoundException("Credential was not found.");
        if (!string.Equals(credential.TypeKey, OAuth2TypeKey, StringComparison.OrdinalIgnoreCase)) return false;

        var data = JsonNode.Parse(secretProtector.Unprotect(credential.ProtectedData)) as JsonObject ?? [];
        var refreshToken = data["refreshToken"]?.GetValue<string>();
        var tokenEndpoint = data["tokenEndpoint"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(tokenEndpoint)) return false;
        if (httpClient is null) throw new InvalidOperationException("OAuth2 credential refresh is not available right now.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/x-www-form-urlencoded" };
        var clientId = data["clientId"]?.GetValue<string>();
        var clientSecret = data["clientSecret"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        }
        var body = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}";

        var response = await httpClient.SendAsync(
            new AutomationHttpRequest(HttpMethod.Post, tokenEndpoint, body, headers), cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"OAuth2 token refresh failed with status {response.StatusCode}.");

        var token = JsonNode.Parse(response.Body) as JsonObject
            ?? throw new InvalidOperationException("OAuth2 token refresh returned an unreadable response.");
        var newAccessToken = token["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(newAccessToken))
            throw new InvalidOperationException("OAuth2 token refresh response did not include an access_token.");

        data["accessToken"] = newAccessToken;
        // Not every provider rotates the refresh token on every refresh - only overwrite the
        // stored one when a new one was actually returned, same "preserveExistingRefreshToken"
        // rule NotionOAuthService.StoreOAuthConnectionAsync already uses.
        var newRefreshToken = token["refresh_token"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(newRefreshToken)) data["refreshToken"] = newRefreshToken;
        if (token["expires_in"] is JsonValue expiresIn && expiresIn.TryGetValue<double>(out var seconds))
            data["expiresAt"] = timeProvider.GetUtcNow().AddSeconds(seconds).ToString("O");

        credential.ProtectedData = secretProtector.Protect(data.ToJsonString());
        credential.LastUsedAt = timeProvider.GetUtcNow();
        credential.UpdatedAt = credential.LastUsedAt;
        credential.UpdatedBy = "automation-oauth-refresh";
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> RefreshExpiringOAuthCredentialsAsync(TimeSpan within, CancellationToken cancellationToken = default)
    {
        var candidateIds = await db.AutomationCredentials.AsNoTracking()
            .Where(item => item.TypeKey == OAuth2TypeKey)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var refreshed = 0;
        var cutoff = timeProvider.GetUtcNow().Add(within);
        foreach (var id in candidateIds)
        {
            var credential = await db.AutomationCredentials.AsNoTracking().FirstAsync(item => item.Id == id, cancellationToken);
            var data = JsonNode.Parse(secretProtector.Unprotect(credential.ProtectedData)) as JsonObject;
            var expiresAtText = data?["expiresAt"]?.GetValue<string>();
            var dueForRefresh = string.IsNullOrWhiteSpace(expiresAtText)
                || (DateTimeOffset.TryParse(expiresAtText, out var expiresAt) && expiresAt <= cutoff);
            if (!dueForRefresh) continue;

            if (await RefreshOAuthCredentialAsync(id, cancellationToken)) refreshed++;
        }
        return refreshed;
    }
}
