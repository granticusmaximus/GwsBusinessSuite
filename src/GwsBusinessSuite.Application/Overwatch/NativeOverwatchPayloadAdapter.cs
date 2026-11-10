using System.Text.Json;

namespace GwsBusinessSuite.Application.Overwatch;

/// <summary>
/// Keeps the native renderer's input lossless. The renderer is intentionally driven by the
/// contract commands rather than by individual provider DTOs, so a native implementation cannot
/// accidentally bypass server-side authorization, filtering, or private-note scoping.
/// </summary>
public sealed class NativeOverwatchPayloadAdapter
{
    private readonly Dictionary<string, JsonElement> _payloads = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JsonElement> Payloads => _payloads;
    public NativeOverwatchViewportRequest? Request { get; private set; }
    public DateTimeOffset? GeneratedAtUtc { get; private set; }

    public void Apply(NativeOverwatchViewport viewport, string expectedContractVersion)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (!string.Equals(viewport.ContractVersion, expectedContractVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Native Overwatch contract {viewport.ContractVersion} is not supported.");
        if (!viewport.Request.Bounds.IsValid)
            throw new ArgumentException("The Overwatch viewport bounds are invalid.", nameof(viewport));

        var next = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var command in viewport.Commands)
        {
            if (!NativeOverwatchOperations.All.Contains(command.Operation, StringComparer.Ordinal))
                throw new InvalidOperationException($"Unknown native Overwatch operation {command.Operation}.");
            next[command.Operation] = command.Payload.Clone();
        }

        _payloads.Clear();
        foreach (var (operation, payload) in next) _payloads.Add(operation, payload);
        Request = viewport.Request;
        GeneratedAtUtc = viewport.GeneratedAtUtc;
    }

    public bool TryGetPayload(string operation, out JsonElement payload) =>
        _payloads.TryGetValue(operation, out payload);
}
