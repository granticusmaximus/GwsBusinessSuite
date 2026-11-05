using System.Runtime.CompilerServices;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;

namespace GwsBusinessSuite.Infrastructure.Services;

/// <summary>
/// Server AI with <c>ServerAi__Mode: None</c> (05-11-26). The droplet has no model. Shared work on
/// public data - inside <see cref="MacRelayScope.ForSharedWork"/> - runs on the Admin Mac that asked
/// the relay most recently, through the same relay SentinelGPT's agent uses. The Mac app already
/// installed answers it. Everything else fails closed exactly as before
/// (<see cref="DisabledOllamaService"/>), and so does shared work while no Admin Mac is online:
/// callers treat <see cref="ServerAiUnavailableException"/> as "wait for the Mac", not an error.
/// Before this, bill overviews, news hot takes and county meeting summaries had stopped (31-10-26)
/// and logged about 1,400 warnings a day.
/// </summary>
public sealed class MacRelayOllamaService(SentinelAgentRelay relay) : IOllamaService
{
    public const string NoMacAlternative = "Open the GWS Suite Mac app, signed in as an admin, and it runs on that Mac.";

    private readonly DisabledOllamaService _closed = new();

    public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        MacRelayScope.SharedWorkAllowed
            ? GenerateOnMacAsync(model, systemPrompt, userPrompt, ct)
            : _closed.GenerateAsync(model, systemPrompt, userPrompt, ct);

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!MacRelayScope.SharedWorkAllowed)
        {
            await foreach (var chunk in _closed.GenerateStreamAsync(model, systemPrompt, userPrompt, ct)) yield return chunk;
            yield break;
        }
        // The relay answers in one piece.
        yield return await GenerateOnMacAsync(model, systemPrompt, userPrompt, ct);
    }

    public Task<OllamaChatResponse> ChatAsync(
        string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default) =>
        MacRelayScope.SharedWorkAllowed ? ChatOnMacAsync(model, messages, tools, ct) : _closed.ChatAsync(model, messages, tools, ct);

    public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => _closed.ListModelsAsync(ct);

    public Task PullModelAsync(string model, CancellationToken ct = default) => _closed.PullModelAsync(model, ct);

    public Task DeleteModelAsync(string model, CancellationToken ct = default) => _closed.DeleteModelAsync(model, ct);

    // The agent relay carries chat only; World News embeds through its own relay.
    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) =>
        _closed.EmbedAsync(model, inputs, ct);

    public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
        _closed.GenerateImageAsync(model, prompt, ct);

    // The model asked for when that Mac has it (as "name" or "name:latest"), otherwise the best one
    // it has that can run SentinelGPT.
    public static string? ModelOn(IReadOnlyList<string> installed, string model) =>
        installed.FirstOrDefault(name => name.Equals(model, StringComparison.OrdinalIgnoreCase)
            || (!model.Contains(':') && name.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)))
        ?? SentinelAgentModels.LocalCandidates(installed).FirstOrDefault();

    private async Task<string> GenerateOnMacAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct) =>
        (await ChatOnMacAsync(model, [new("system", systemPrompt), new("user", userPrompt)], null, ct)).Content.Trim();

    private async Task<OllamaChatResponse> ChatOnMacAsync(
        string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools, CancellationToken ct)
    {
        if (relay.SharedWorkMac() is not { } mac || ModelOn(mac.Models, model) is not { } macModel)
            throw new ServerAiUnavailableException("Server AI", NoMacAlternative);
        var payload = OllamaChatPayload.Build(macModel, messages, tools, "30m").ToJsonString();
        return OllamaChatPayload.Parse(await relay.ChatSharedAsync(mac.Username, payload, ct));
    }
}
