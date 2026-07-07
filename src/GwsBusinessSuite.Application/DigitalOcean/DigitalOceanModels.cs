namespace GwsBusinessSuite.Application.DigitalOcean;

public sealed class DigitalOceanSettingsView
{
    public string ApiToken { get; init; } = string.Empty;
    public string DropletId { get; init; } = string.Empty;

    // Set when a stored token exists but can no longer be decrypted (e.g. the Data
    // Protection key ring rotated) - same convention as CjConnectorSettingsView.
    public bool ApiTokenUnreadable { get; init; }
}

public sealed class DropletInfoView
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string? PublicIpAddress { get; init; }
    public string? MemoryDescription { get; init; }
    public string? DiskDescription { get; init; }
    public string? VcpuDescription { get; init; }
}

// Returned instead of throwing when the DO connection isn't configured or the API call
// fails, so the page can show a clear message instead of crashing.
public sealed record DropletInfoResult(bool Available, DropletInfoView? Droplet, string? UnavailableReason = null);

public sealed class DropletActionView
{
    public long Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record DigitalOceanActionResult(bool Succeeded, string Message);
