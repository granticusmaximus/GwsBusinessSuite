namespace GwsBusinessSuite.Application.DigitalOcean;

public sealed class DigitalOceanSettingsView
{
    public string ApiToken { get; init; } = string.Empty;
    public string DropletId { get; init; } = string.Empty;

    // Set when a stored token exists but can no longer be decrypted (e.g. the Data
    // Protection key ring rotated) - same convention as CjConnectorSettingsView.
    public bool ApiTokenUnreadable { get; init; }

    public string SshUsername { get; init; } = "root";
    public int SshPort { get; init; } = 22;

    // The private key itself never round-trips back to the browser - it's more
    // sensitive than ApiToken, so unlike ApiToken this view only ever exposes whether
    // one is saved, not its value.
    public bool HasPrivateKey { get; init; }
    public bool SshPrivateKeyUnreadable { get; init; }
    public string? SshHostKeyFingerprint { get; init; }
}

// Save-side DTO for the SSH connection fields, kept separate from
// DigitalOceanSettingsView because the private key has different "blank" semantics:
// a blank ApiToken on save just re-encrypts blank, but a blank NewPrivateKey must mean
// "leave the existing key untouched" since the field is never prefilled with the
// current value.
public sealed class SshSettingsInput
{
    public string Username { get; init; } = "root";
    public int Port { get; init; } = 22;
    public string? NewPrivateKey { get; init; }
    public string? NewPrivateKeyPassphrase { get; init; }
    public bool ClearPrivateKey { get; init; }
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
