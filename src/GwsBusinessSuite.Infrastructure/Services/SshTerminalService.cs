using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Application.SshTerminal;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SshTerminalService(
    IAppDbContext dbContext,
    IDigitalOceanService digitalOceanService,
    ISecretProtector secretProtector,
    ILogger<SshTerminalService> logger) : ISshTerminalService
{
    public async Task<SshTerminalOpenResult> OpenAsync(
        string performedBy, int initialColumns, int initialRows, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.DigitalOceanSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.SshPrivateKey))
        {
            return new SshTerminalOpenResult(false, null, "No SSH private key saved yet. Add one below.");
        }

        string privateKeyPem;
        string? passphrase;
        try
        {
            privateKeyPem = secretProtector.Unprotect(row.SshPrivateKey);
            passphrase = string.IsNullOrEmpty(row.SshPrivateKeyPassphrase)
                ? null
                : secretProtector.Unprotect(row.SshPrivateKeyPassphrase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to decrypt the stored SSH private key. The key ring may have changed since it was saved.");
            return new SshTerminalOpenResult(false, null, "The saved private key could not be decrypted. Re-enter it below.");
        }

        var dropletResult = await digitalOceanService.GetDropletInfoAsync(cancellationToken);
        if (!dropletResult.Available || dropletResult.Droplet?.PublicIpAddress is not { } host)
        {
            return new SshTerminalOpenResult(
                false, null, dropletResult.UnavailableReason ?? "The droplet's public IP address isn't available yet.");
        }

        SshClient client;
        try
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem));
            var keyFile = passphrase is null
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, passphrase);
            var authMethod = new PrivateKeyAuthenticationMethod(row.SshUsername, keyFile);
            var connectionInfo = new ConnectionInfo(host, row.SshPort, row.SshUsername, authMethod)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client = new SshClient(connectionInfo);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build an SSH connection from the saved private key.");
            return new SshTerminalOpenResult(false, null, $"Invalid private key: {ex.Message}");
        }

        // Host key pinning: nothing stored yet -> trust and pin on this connect. A stored
        // fingerprint that doesn't match is rejected outright (protects against DigitalOcean
        // IP reuse / a MITM on the network path, since SSH.NET trusts nothing by default).
        var expectedFingerprint = row.SshHostKeyFingerprint;
        string? actualFingerprint = null;
        var hostKeyMismatch = false;

        client.HostKeyReceived += (_, e) =>
        {
            actualFingerprint = e.FingerPrintSHA256;
            e.CanTrust = IsHostKeyTrusted(expectedFingerprint, actualFingerprint);
            hostKeyMismatch = !e.CanTrust;
        };

        try
        {
            client.Connect();
        }
        catch (SshConnectionException) when (hostKeyMismatch)
        {
            client.Dispose();
            await LogAsync("SshConnect", false,
                $"Host key mismatch: expected {expectedFingerprint}, got {actualFingerprint}.", performedBy, cancellationToken);
            return new SshTerminalOpenResult(
                false, null,
                "The droplet's SSH host key doesn't match the one pinned on first connect. This could mean the droplet was rebuilt, or could indicate the connection is being intercepted.",
                HostKeyMismatch: true, ExpectedFingerprint: expectedFingerprint, ActualFingerprint: actualFingerprint);
        }
        catch (Exception ex)
        {
            client.Dispose();
            logger.LogWarning(ex, "Failed to connect over SSH.");
            await LogAsync("SshConnect", false, ex.Message, performedBy, cancellationToken);
            return new SshTerminalOpenResult(false, null, $"Could not connect: {ex.Message}");
        }

        if (string.IsNullOrEmpty(expectedFingerprint) && actualFingerprint is not null)
        {
            await PinHostKeyAsync(actualFingerprint, cancellationToken);
        }

        ShellStream shellStream;
        try
        {
            shellStream = client.CreateShellStream("xterm-256color", (uint)initialColumns, (uint)initialRows, 800, 600, 4096);
        }
        catch (Exception ex)
        {
            client.Disconnect();
            client.Dispose();
            logger.LogWarning(ex, "Failed to open an interactive shell over SSH.");
            await LogAsync("SshConnect", false, ex.Message, performedBy, cancellationToken);
            return new SshTerminalOpenResult(false, null, $"Connected, but could not open a shell: {ex.Message}");
        }

        await LogAsync("SshConnect", true, $"Connected as {row.SshUsername}@{host}:{row.SshPort}.", performedBy, cancellationToken);

        var session = new SshTerminalSession(client, shellStream, performedBy, LogAsync);
        return new SshTerminalOpenResult(true, session, null);
    }

    // Pure decision, factored out for unit testing without a live SSH connection: no
    // fingerprint pinned yet -> trust (and the caller pins it on this connect); a pinned
    // fingerprint that doesn't match the incoming one -> reject.
    public static bool IsHostKeyTrusted(string? storedFingerprint, string incomingFingerprint) =>
        string.IsNullOrEmpty(storedFingerprint) || string.Equals(storedFingerprint, incomingFingerprint, StringComparison.Ordinal);

    public Task TrustHostKeyAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        PinHostKeyAsync(fingerprint, cancellationToken, updatedBy: "user");

    private async Task PinHostKeyAsync(string fingerprint, CancellationToken cancellationToken, string updatedBy = "system")
    {
        var row = await dbContext.DigitalOceanSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return;
        }

        row.SshHostKeyFingerprint = fingerprint;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = updatedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task LogAsync(string action, bool succeeded, string resultSummary, string performedBy, CancellationToken cancellationToken)
    {
        dbContext.DockerActionLogs.Add(new DockerActionLog
        {
            ContainerName = "droplet",
            Action = action,
            Succeeded = succeeded,
            ResultSummary = resultSummary,
            PerformedBy = performedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = performedBy
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class SshTerminalSession : ISshTerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _shellStream;
    private readonly string _performedBy;
    private readonly Func<string, bool, string, string, CancellationToken, Task> _logAsync;
    private readonly EventHandler<ShellDataEventArgs> _dataHandler;
    private readonly EventHandler<EventArgs> _closedHandler;
    private int _disposed;

    public event Action<byte[]>? OutputReceived;
    public event Action<string>? Closed;

    public bool IsConnected => _client.IsConnected;

    public SshTerminalSession(
        SshClient client, ShellStream shellStream, string performedBy,
        Func<string, bool, string, string, CancellationToken, Task> logAsync)
    {
        _client = client;
        _shellStream = shellStream;
        _performedBy = performedBy;
        _logAsync = logAsync;

        _dataHandler = (_, e) => OutputReceived?.Invoke(e.Data);
        _shellStream.DataReceived += _dataHandler;

        _closedHandler = (_, _) => Closed?.Invoke("The SSH connection closed unexpectedly.");
        _shellStream.Closed += _closedHandler;
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default) =>
        _shellStream.WriteAsync(data, 0, data.Length, cancellationToken);

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        // Client-side dimensions only affect rendering; width/height in pixels aren't
        // meaningful for a text terminal so a nominal value is fine here.
        _shellStream.ChangeWindowSize((uint)columns, (uint)rows, 800, 600);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shellStream.DataReceived -= _dataHandler;
        _shellStream.Closed -= _closedHandler;

        var wasConnected = _client.IsConnected;
        try
        {
            _shellStream.Dispose();
        }
        catch
        {
            // best-effort teardown
        }

        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch
        {
            // best-effort teardown
        }

        _client.Dispose();

        if (wasConnected)
        {
            await _logAsync("SshDisconnect", true, "Session closed.", _performedBy, CancellationToken.None);
        }
    }
}
