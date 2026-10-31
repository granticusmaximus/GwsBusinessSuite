using System.Runtime.CompilerServices;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Infrastructure.Services;

/// <summary>
/// Fail-closed model runtime used when production delegates inference to the Mac app.
/// Keeping the interface registered lets non-AI application services resolve normally while
/// ensuring an overlooked server-side call cannot silently reconnect to an Ollama instance.
/// </summary>
public sealed class DisabledOllamaService : IOllamaService
{
    public const string Message =
        "Server AI is disabled. Open the GWS Suite Mac app to run this work on your Mac.";

    public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        Task.FromException<string>(CreateException());

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        throw CreateException();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>([]);

    public Task PullModelAsync(string model, CancellationToken ct = default) => Task.FromException(CreateException());

    public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.FromException(CreateException());

    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<float[]>>(CreateException());

    public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
        Task.FromException<string>(CreateException());

    public Task<OllamaChatResponse> ChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<OllamaToolDefinition>? tools = null,
        CancellationToken ct = default) => Task.FromException<OllamaChatResponse>(CreateException());

    private static ServerAiUnavailableException CreateException() =>
        new("AI on this server", "Open the GWS Suite Mac app to run this work on your Mac.");
}
