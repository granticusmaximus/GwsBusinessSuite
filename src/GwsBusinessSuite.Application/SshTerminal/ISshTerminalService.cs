namespace GwsBusinessSuite.Application.SshTerminal;

// Opens an interactive SSH session on the one droplet this app already knows about
// (host/credentials are resolved internally from DigitalOceanSettings + the droplet's
// public IP - callers never pass connection details). No Renci.SshNet types appear
// here; the concrete SSH library is an Infrastructure concern.
public interface ISshTerminalService
{
    Task<SshTerminalOpenResult> OpenAsync(
        string performedBy,
        int initialColumns,
        int initialRows,
        CancellationToken cancellationToken = default);

    // Explicit override after a HostKeyMismatch result - the admin has manually verified
    // the new fingerprint (e.g. after rebuilding the droplet) and wants it pinned in place
    // of the old one, rather than the mismatch being silently trusted automatically.
    Task TrustHostKeyAsync(string fingerprint, CancellationToken cancellationToken = default);
}

public sealed record SshTerminalOpenResult(
    bool Succeeded,
    ISshTerminalSession? Session,
    string? FailureReason,
    bool HostKeyMismatch = false,
    string? ExpectedFingerprint = null,
    string? ActualFingerprint = null);

public interface ISshTerminalSession : IAsyncDisposable
{
    // Fires from the background SSH read loop, not the Blazor renderer thread - callers
    // must marshal onto the circuit via InvokeAsync before touching JS interop or state.
    event Action<byte[]>? OutputReceived;

    // Fires once if the connection drops unexpectedly (not via DisposeAsync).
    event Action<string>? Closed;

    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    // Best-effort: the underlying SSH.NET ShellStream has no public API to send a PTY
    // window-change request after creation, so this only ever reflows the client-side
    // xterm.js view via its own fit addon. Server-side tools that query terminal size
    // (stty size, top, vim) may see a stale size until reconnect.
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);

    bool IsConnected { get; }
}
