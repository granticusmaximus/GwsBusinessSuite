using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class DigitalOceanService(
    HttpClient httpClient,
    IAppDbContext dbContext,
    ISecretProtector secretProtector,
    ILogger<DigitalOceanService> logger,
    ICurrentUserAccessor? currentUserAccessor = null,
    IDockerHealthService? dockerHealthService = null) : IDigitalOceanService
{
    // DigitalOcean's local droplet metadata service - only reachable from inside the
    // droplet itself. Used to auto-detect the droplet's own ID so the user doesn't have
    // to look it up manually in production; unreachable in local dev, where a manual
    // DropletId override is required instead.
    private const string MetadataDropletIdUrl = "http://169.254.169.254/metadata/v1/id";
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    public async Task<DigitalOceanSettingsView?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var isUnreadable = !string.IsNullOrWhiteSpace(row.ApiToken) && UnprotectApiToken(row.ApiToken).IsUnreadable;
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
            DropletId = row.DropletId,
            HasApiToken = !string.IsNullOrWhiteSpace(row.ApiToken),
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

    public async Task SaveSettingsAsync(DigitalOceanApiSettingsInput settings, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new DigitalOceanSettings();
            dbContext.DigitalOceanSettings.Add(row);
        }

        if (settings.ClearApiToken)
        {
            row.ApiToken = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(settings.NewApiToken))
        {
            row.ApiToken = ProtectApiToken(settings.NewApiToken);
        }
        // else: no new token supplied, leave the existing stored token untouched -
        // the browser is never shown the real value, so a blank field just means
        // "nothing was typed", not "clear it" (mirrors SaveSshSettingsAsync below).

        row.DropletId = settings.DropletId.Trim();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveSshSettingsAsync(SshSettingsInput settings, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(cancellationToken);
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
        row.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);

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

    public async Task<ActiveDropletsImagesResult> ListActiveDropletsWithImagesAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.ApiToken))
        {
            return new ActiveDropletsImagesResult(false, [], "DigitalOcean isn't connected yet. Add an API token below.");
        }

        var (apiToken, isTokenUnreadable) = UnprotectApiToken(row.ApiToken);
        if (isTokenUnreadable || string.IsNullOrWhiteSpace(apiToken))
        {
            return new ActiveDropletsImagesResult(false, [], "Saved DigitalOcean API token could not be decrypted. Re-enter it below.");
        }

        IReadOnlyList<DropletDto> droplets;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "droplets?per_page=200");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ActiveDropletsImagesResult(false, [], $"DigitalOcean API returned {(int)response.StatusCode}. Check the API token.");
            }

            var payload = await response.Content.ReadFromJsonAsync<DropletsListResponse>(cancellationToken);
            if (payload?.Droplets is null)
            {
                return new ActiveDropletsImagesResult(false, [], "DigitalOcean API returned an unexpected response.");
            }

            droplets = payload.Droplets
                .Where(d => string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to list DigitalOcean droplets.");
            return new ActiveDropletsImagesResult(false, [], "Could not reach the DigitalOcean API.");
        }

        var currentDropletId = !string.IsNullOrWhiteSpace(row.DropletId)
            ? row.DropletId.Trim()
            : await TryAutoDetectDropletIdAsync(cancellationToken);

        var sshCredentials = TryLoadSshCredentials(row);
        var views = await Task.WhenAll(droplets.Select(droplet =>
            InspectDropletAsync(droplet, currentDropletId, row, sshCredentials, cancellationToken)));

        return new ActiveDropletsImagesResult(true, views);
    }

    public static IReadOnlyList<DropletContainerImageView> ParseDockerPsOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var results = new List<DropletContainerImageView>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0].Trim();
            var image = parts[1].Trim();
            var state = parts.Length >= 3 ? parts[2].Trim() : "running";
            var status = parts.Length >= 4 ? parts[3].Trim() : state;
            if (name.Length == 0 || image.Length == 0)
            {
                continue;
            }

            results.Add(new DropletContainerImageView(name, image, state, status));
        }

        return results.OrderBy(c => c.ContainerName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<ActiveDropletView> InspectDropletAsync(
        DropletDto droplet,
        string? currentDropletId,
        DigitalOceanSettings settings,
        SshCredentialsResult sshCredentials,
        CancellationToken cancellationToken)
    {
        var publicIp = droplet.Networks?.V4?.FirstOrDefault(n => n.Type == "public")?.IpAddress;
        var privateIp = droplet.Networks?.V4?.FirstOrDefault(n => n.Type == "private")?.IpAddress;
        var isCurrentHost = !string.IsNullOrWhiteSpace(currentDropletId)
            && string.Equals(droplet.Id.ToString(), currentDropletId, StringComparison.Ordinal);

        IReadOnlyList<DropletContainerImageView> containerImages = [];
        string? probeStatus = null;

        if (isCurrentHost && dockerHealthService is not null)
        {
            var local = await dockerHealthService.ListContainersAsync(cancellationToken);
            if (local.Available)
            {
                containerImages = local.Containers
                    .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
                    .Select(c => new DropletContainerImageView(c.Name, c.Image, c.State, c.Status))
                    .OrderBy(c => c.ContainerName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (containerImages.Count == 0)
                {
                    probeStatus = "No running Docker containers.";
                }
                return BuildActiveDropletView(droplet, publicIp, privateIp, isCurrentHost, containerImages, probeStatus);
            }
        }

        if (!sshCredentials.HasKey)
        {
            probeStatus = "Add an SSH private key in Terminal settings to inspect running Docker images on remote droplets.";
        }
        else if (sshCredentials.Error is not null)
        {
            probeStatus = sshCredentials.Error;
        }
        else
        {
            var hosts = new[] { publicIp, privateIp }
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
            if (hosts.Count == 0)
            {
                probeStatus = "No IP address available for SSH inspection.";
            }
            else
            {
                var username = string.IsNullOrWhiteSpace(settings.SshUsername) ? "root" : settings.SshUsername;
                var port = settings.SshPort <= 0 ? 22 : settings.SshPort;
                var pinnedFingerprint = isCurrentHost ? settings.SshHostKeyFingerprint : null;
                (containerImages, probeStatus) = await QueryRemoteDockerPsAsync(
                    hosts, port, username, sshCredentials.PrivateKeyPem!, sshCredentials.Passphrase, pinnedFingerprint, cancellationToken);
            }
        }

        return BuildActiveDropletView(droplet, publicIp, privateIp, isCurrentHost, containerImages, probeStatus);
    }

    private static ActiveDropletView BuildActiveDropletView(
        DropletDto droplet,
        string? publicIp,
        string? privateIp,
        bool isCurrentHost,
        IReadOnlyList<DropletContainerImageView> containerImages,
        string? probeStatus) => new()
        {
            Id = droplet.Id,
            Name = droplet.Name,
            Status = droplet.Status,
            Region = droplet.Region?.Name ?? droplet.Region?.Slug ?? "unknown",
            Size = droplet.SizeSlug,
            PublicIpAddress = publicIp,
            PrivateIpAddress = privateIp,
            MemoryDescription = droplet.Size?.Memory is { } mem ? $"{mem / 1024.0:0.#} GB RAM" : null,
            DiskDescription = droplet.Size?.Disk is { } disk ? $"{disk} GB disk" : null,
            VcpuDescription = droplet.Vcpus is { } vcpus ? $"{vcpus} vCPU" : null,
            BaseImageName = FormatBaseImageName(droplet.Image),
            BaseImageSlug = droplet.Image?.Slug,
            BaseImageType = droplet.Image?.Type,
            IsCurrentHost = isCurrentHost,
            ActiveContainerImages = containerImages,
            ContainerProbeStatus = probeStatus
        };

    private static string FormatBaseImageName(DropletImageDto? image)
    {
        if (image is null) return "Unknown";
        var dist = image.Distribution?.Trim();
        var name = image.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(dist) ? "Unknown" : dist;
        if (string.IsNullOrEmpty(dist) || name.StartsWith(dist, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{dist} {name}";
    }

    private sealed record SshCredentialsResult(bool HasKey, string? PrivateKeyPem, string? Passphrase, string? Error);

    private SshCredentialsResult TryLoadSshCredentials(DigitalOceanSettings row)
    {
        if (string.IsNullOrWhiteSpace(row.SshPrivateKey))
        {
            return new SshCredentialsResult(false, null, null, null);
        }

        try
        {
            var pem = secretProtector.Unprotect(row.SshPrivateKey);
            var passphrase = string.IsNullOrEmpty(row.SshPrivateKeyPassphrase)
                ? null
                : secretProtector.Unprotect(row.SshPrivateKeyPassphrase);
            return new SshCredentialsResult(true, pem, passphrase, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to decrypt stored SSH private key for droplet image inspection.");
            return new SshCredentialsResult(true, null, null, "Saved SSH private key could not be decrypted.");
        }
    }

    private async Task<(IReadOnlyList<DropletContainerImageView> Images, string? ProbeStatus)> QueryRemoteDockerPsAsync(
        IReadOnlyList<string> hosts,
        int port,
        string username,
        string privateKeyPem,
        string? passphrase,
        string? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        string? lastError = null;
        foreach (var host in hosts)
        {
            try
            {
                return await Task.Run(() =>
                {
                    using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem));
                    var keyFile = passphrase is null
                        ? new PrivateKeyFile(keyStream)
                        : new PrivateKeyFile(keyStream, passphrase);
                    var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
                    var connectionInfo = new ConnectionInfo(host, port, username, authMethod)
                    {
                        Timeout = TimeSpan.FromSeconds(5)
                    };
                    using var client = new SshClient(connectionInfo);
                    client.HostKeyReceived += (_, e) =>
                    {
                        if (string.IsNullOrEmpty(expectedFingerprint))
                        {
                            e.CanTrust = true;
                            return;
                        }

                        e.CanTrust = SshTerminalService.IsHostKeyTrusted(expectedFingerprint, e.FingerPrintSHA256);
                    };

                    client.Connect();
                    try
                    {
                        using var cmd = client.CreateCommand("docker ps --format '{{.Names}}\\t{{.Image}}\\t{{.State}}\\t{{.Status}}'");
                        cmd.CommandTimeout = TimeSpan.FromSeconds(10);
                        var output = cmd.Execute();
                        if (cmd.ExitStatus != 0)
                        {
                            var err = cmd.Error?.Trim() ?? string.Empty;
                            if (err.Contains("not found", StringComparison.OrdinalIgnoreCase))
                            {
                                return ((IReadOnlyList<DropletContainerImageView>)[], "Docker is not installed on this droplet.");
                            }
                            return ((IReadOnlyList<DropletContainerImageView>)[], string.IsNullOrEmpty(err) ? $"docker ps exited with code {cmd.ExitStatus}." : err);
                        }

                        var parsed = ParseDockerPsOutput(output);
                        return (parsed, parsed.Count == 0 ? "No running Docker containers." : null);
                    }
                    finally
                    {
                        if (client.IsConnected) client.Disconnect();
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is SshConnectionException or SshException or System.Net.Sockets.SocketException or TimeoutException)
            {
                lastError = $"SSH to {host} failed: {ex.Message}";
            }
        }

        return ([], lastError ?? "Could not reach droplet over SSH.");
    }

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
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(cancellationToken);
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

    private sealed record DropletsListResponse([property: JsonPropertyName("droplets")] IReadOnlyList<DropletDto>? Droplets);

    private sealed record DropletDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("size_slug")] string SizeSlug,
        [property: JsonPropertyName("vcpus")] int? Vcpus,
        [property: JsonPropertyName("region")] DropletRegionDto? Region,
        [property: JsonPropertyName("size")] DropletSizeDto? Size,
        [property: JsonPropertyName("networks")] DropletNetworksDto? Networks,
        [property: JsonPropertyName("image")] DropletImageDto? Image);

    private sealed record DropletImageDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("distribution")] string? Distribution,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("type")] string? Type);

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
