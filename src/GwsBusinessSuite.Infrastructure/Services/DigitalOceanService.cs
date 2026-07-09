using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DigitalOceanService(
    HttpClient httpClient,
    IAppDbContext dbContext,
    ISecretProtector secretProtector,
    ILogger<DigitalOceanService> logger) : IDigitalOceanService
{
    // DigitalOcean's local droplet metadata service - only reachable from inside the
    // droplet itself. Used to auto-detect the droplet's own ID so the user doesn't have
    // to look it up manually in production; unreachable in local dev, where a manual
    // DropletId override is required instead.
    private const string MetadataDropletIdUrl = "http://169.254.169.254/metadata/v1/id";

    public async Task<DigitalOceanSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var (apiToken, isUnreadable) = UnprotectApiToken(row.ApiToken);
        var sshPrivateKeyUnreadable = false;
        if (!string.IsNullOrWhiteSpace(row.SshPrivateKey))
        {
            try
            {
                secretProtector.Unprotect(row.SshPrivateKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to decrypt stored SSH private key. The key ring may have changed since it was saved.");
                sshPrivateKeyUnreadable = true;
            }
        }

        return new DigitalOceanSettingsView
        {
            ApiToken = apiToken,
            DropletId = row.DropletId,
            ApiTokenUnreadable = isUnreadable,
            // Existing rows created before the SSH columns existed have "" / 0 from the
            // migration's column defaults (EF uses the CLR default, not the C# property
            // initializer) rather than the app's intended "root" / 22 defaults.
            SshUsername = string.IsNullOrWhiteSpace(row.SshUsername) ? "root" : row.SshUsername,
            SshPort = row.SshPort <= 0 ? 22 : row.SshPort,
            HasPrivateKey = !string.IsNullOrWhiteSpace(row.SshPrivateKey),
            SshPrivateKeyUnreadable = sshPrivateKeyUnreadable,
            SshHostKeyFingerprint = row.SshHostKeyFingerprint
        };
    }

    public async Task SaveSettingsAsync(DigitalOceanSettingsView settings, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new DigitalOceanSettings();
            dbContext.DigitalOceanSettings.Add(row);
        }

        row.ApiToken = ProtectApiToken(settings.ApiToken);
        row.DropletId = settings.DropletId.Trim();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = "user";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveSshSettingsAsync(SshSettingsInput settings, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new DigitalOceanSettings();
            dbContext.DigitalOceanSettings.Add(row);
        }

        row.SshUsername = string.IsNullOrWhiteSpace(settings.Username) ? "root" : settings.Username.Trim();
        row.SshPort = settings.Port <= 0 ? 22 : settings.Port;

        if (settings.ClearPrivateKey)
        {
            row.SshPrivateKey = string.Empty;
            row.SshPrivateKeyPassphrase = null;
            row.SshHostKeyFingerprint = null;
        }
        else if (!string.IsNullOrWhiteSpace(settings.NewPrivateKey))
        {
            // Note: the pinned SshHostKeyFingerprint is intentionally left untouched here -
            // it identifies the remote server, not the credential used to authenticate to
            // it, so rotating the private key doesn't invalidate the existing pin.
            row.SshPrivateKey = secretProtector.Protect(settings.NewPrivateKey.Trim());
            row.SshPrivateKeyPassphrase = string.IsNullOrEmpty(settings.NewPrivateKeyPassphrase)
                ? null
                : secretProtector.Protect(settings.NewPrivateKeyPassphrase);
        }
        // else: NewPrivateKey left blank - leave the existing stored key untouched.

        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = "user";

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DropletInfoResult> GetDropletInfoAsync(CancellationToken cancellationToken = default)
    {
        var context = await BuildRequestContextAsync(cancellationToken);
        if (context is null)
        {
            return new DropletInfoResult(false, null, "DigitalOcean isn't connected yet. Add an API token and droplet ID below.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"droplets/{context.DropletId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiToken);
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DropletInfoResult(false, null, $"DigitalOcean API returned {(int)response.StatusCode}. Check the API token and droplet ID.");
            }

            var payload = await response.Content.ReadFromJsonAsync<DropletResponse>(cancellationToken);
            var droplet = payload?.Droplet;
            if (droplet is null)
            {
                return new DropletInfoResult(false, null, "DigitalOcean API returned an unexpected response.");
            }

            var publicIp = droplet.Networks?.V4?.FirstOrDefault(n => n.Type == "public")?.IpAddress;

            return new DropletInfoResult(true, new DropletInfoView
            {
                Name = droplet.Name,
                Status = droplet.Status,
                Region = droplet.Region?.Name ?? droplet.Region?.Slug ?? "unknown",
                Size = droplet.SizeSlug,
                PublicIpAddress = publicIp,
                MemoryDescription = droplet.Size?.Memory is { } mem ? $"{mem / 1024.0:0.#} GB RAM" : null,
                DiskDescription = droplet.Size?.Disk is { } disk ? $"{disk} GB disk" : null,
                VcpuDescription = droplet.Vcpus is { } vcpus ? $"{vcpus} vCPU" : null
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to reach the DigitalOcean API.");
            return new DropletInfoResult(false, null, "Could not reach the DigitalOcean API.");
        }
    }

    public async Task<IReadOnlyList<DropletActionView>> ListRecentActionsAsync(CancellationToken cancellationToken = default)
    {
        var context = await BuildRequestContextAsync(cancellationToken);
        if (context is null)
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"droplets/{context.DropletId}/actions?per_page=20");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiToken);
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<DropletActionsResponse>(cancellationToken);
            return payload?.Actions?
                .OrderByDescending(a => a.StartedAt)
                .Select(a => new DropletActionView
                {
                    Id = a.Id,
                    Type = a.Type,
                    Status = a.Status,
                    StartedAt = a.StartedAt,
                    CompletedAt = a.CompletedAt
                })
                .ToList()
                ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to list DigitalOcean droplet actions.");
            return [];
        }
    }

    public async Task<DigitalOceanActionResult> RebootDropletAsync(string performedBy, CancellationToken cancellationToken = default) =>
        await PostActionAsync("reboot", null, performedBy, cancellationToken);

    public async Task<DigitalOceanActionResult> ResizeDropletAsync(string newSize, bool resizeDisk, string performedBy, CancellationToken cancellationToken = default) =>
        await PostActionAsync("resize", new { size = newSize, disk = resizeDisk }, performedBy, cancellationToken);

    public async Task<DigitalOceanActionResult> CreateSnapshotAsync(string snapshotName, string performedBy, CancellationToken cancellationToken = default) =>
        await PostActionAsync("snapshot", new { name = snapshotName }, performedBy, cancellationToken);

    private async Task<DigitalOceanActionResult> PostActionAsync(string type, object? extraFields, string performedBy, CancellationToken cancellationToken)
    {
        var context = await BuildRequestContextAsync(cancellationToken);
        if (context is null)
        {
            return new DigitalOceanActionResult(false, "DigitalOcean isn't connected yet.");
        }

        try
        {
            var body = extraFields is null
                ? new Dictionary<string, object?> { ["type"] = type }
                : MergeTypeIntoBody(type, extraFields);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"droplets/{context.DropletId}/actions")
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiToken);
            var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = $"DigitalOcean API returned {(int)response.StatusCode}: {errorBody}";
                await LogActionAsync(type, false, message, performedBy, cancellationToken);
                return new DigitalOceanActionResult(false, message);
            }

            var payload = await response.Content.ReadFromJsonAsync<DropletActionEnvelope>(cancellationToken);
            var message2 = payload?.Action is { } action
                ? $"Action queued (id {action.Id}, status: {action.Status})."
                : "Action queued.";
            await LogActionAsync(type, true, message2, performedBy, cancellationToken);
            return new DigitalOceanActionResult(true, message2);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to submit DigitalOcean droplet action {Type}.", type);
            await LogActionAsync(type, false, ex.Message, performedBy, cancellationToken);
            return new DigitalOceanActionResult(false, ex.Message);
        }
    }

    private static Dictionary<string, object?> MergeTypeIntoBody(string type, object extraFields)
    {
        var body = new Dictionary<string, object?> { ["type"] = type };
        foreach (var prop in extraFields.GetType().GetProperties())
        {
            body[prop.Name] = prop.GetValue(extraFields);
        }
        return body;
    }

    private async Task LogActionAsync(string action, bool succeeded, string resultSummary, string performedBy, CancellationToken cancellationToken)
    {
        dbContext.DockerActionLogs.Add(new DockerActionLog
        {
            ContainerName = "droplet",
            Action = char.ToUpperInvariant(action[0]) + action[1..],
            Succeeded = succeeded,
            ResultSummary = resultSummary,
            PerformedBy = performedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = performedBy
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record RequestContext(string ApiToken, string DropletId);

    private async Task<RequestContext?> BuildRequestContextAsync(CancellationToken cancellationToken)
    {
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.ApiToken))
        {
            return null;
        }

        var (apiToken, isUnreadable) = UnprotectApiToken(row.ApiToken);
        if (isUnreadable || string.IsNullOrWhiteSpace(apiToken))
        {
            return null;
        }

        var dropletId = row.DropletId;
        if (string.IsNullOrWhiteSpace(dropletId))
        {
            dropletId = await TryAutoDetectDropletIdAsync(cancellationToken);
        }

        return string.IsNullOrWhiteSpace(dropletId) ? null : new RequestContext(apiToken, dropletId);
    }

    private async Task<string?> TryAutoDetectDropletIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var metadataClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var id = await metadataClient.GetStringAsync(MetadataDropletIdUrl, cancellationToken);
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Expected in local dev, where the metadata service doesn't exist.
            return null;
        }
    }

    private string ProtectApiToken(string apiToken) =>
        string.IsNullOrWhiteSpace(apiToken) ? string.Empty : secretProtector.Protect(apiToken.Trim());

    private (string ApiToken, bool IsUnreadable) UnprotectApiToken(string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return (string.Empty, false);
        }

        try
        {
            return (secretProtector.Unprotect(storedValue), false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to decrypt stored DigitalOcean API token. The key ring may have changed since it was saved.");
            return (string.Empty, true);
        }
    }

    // ── DigitalOcean API v2 JSON DTOs ────────────────────────────

    private sealed record DropletResponse([property: JsonPropertyName("droplet")] DropletDto? Droplet);

    private sealed record DropletDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("size_slug")] string SizeSlug,
        [property: JsonPropertyName("vcpus")] int? Vcpus,
        [property: JsonPropertyName("region")] DropletRegionDto? Region,
        [property: JsonPropertyName("size")] DropletSizeDto? Size,
        [property: JsonPropertyName("networks")] DropletNetworksDto? Networks);

    private sealed record DropletRegionDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("slug")] string? Slug);

    private sealed record DropletSizeDto(
        [property: JsonPropertyName("memory")] int? Memory,
        [property: JsonPropertyName("disk")] int? Disk);

    private sealed record DropletNetworksDto([property: JsonPropertyName("v4")] IReadOnlyList<DropletNetworkV4Dto>? V4);

    private sealed record DropletNetworkV4Dto(
        [property: JsonPropertyName("ip_address")] string? IpAddress,
        [property: JsonPropertyName("type")] string? Type);

    private sealed record DropletActionsResponse([property: JsonPropertyName("actions")] IReadOnlyList<DropletActionDto>? Actions);

    private sealed record DropletActionEnvelope([property: JsonPropertyName("action")] DropletActionDto? Action);

    private sealed record DropletActionDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);
}
