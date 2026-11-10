using System.Text.Json;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.App;

/// <summary>
/// Reads the native renderer contract through the Workspace WebView's authenticated session.
/// It deliberately does not own another cookie jar or device credential.
/// </summary>
public sealed class NativeOverwatchContractClient(NativeSessionClient session)
{
    public const string ContractPath = "/api/v1/native-overwatch/contract";

    public async Task<(NativeOverwatchContractDescriptor? Contract, string? Error)> GetContractAsync(
        CancellationToken cancellationToken = default)
    {
        var (json, error) = await session.GetNativeOverwatchContractAsync(cancellationToken);
        if (json is not { ValueKind: JsonValueKind.Object } body) return (null, error ?? "The server sent an invalid native Overwatch contract.");

        try
        {
            var version = body.GetProperty("version").GetString();
            var operations = body.GetProperty("operations").EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
            return (new NativeOverwatchContractDescriptor(version ?? string.Empty, operations), null);
        }
        catch (KeyNotFoundException)
        {
            return (null, "The server sent an invalid native Overwatch contract.");
        }
    }
}

public sealed record NativeOverwatchContractDescriptor(string Version, IReadOnlyList<string> Operations)
{
    public bool SupportsAllMappedOperations =>
        Operations.Count == NativeOverwatchOperations.All.Count
        && !Operations.Except(NativeOverwatchOperations.All, StringComparer.Ordinal).Any()
        && !NativeOverwatchOperations.All.Except(Operations, StringComparer.Ordinal).Any();
}
