using System.Text.Json;

namespace GwsBusinessSuite.OllamaKit;

// Unions two or more tool sources (e.g. the native app's wiki tools + a workspace's file tools)
// into one IOllamaToolExecutor, so a host can offer the model a single merged tool set instead of
// switching between mutually-exclusive executors behind a mode flag. Definitions is read fresh on
// every round by OllamaToolCallingAgent, so a source that varies its own Definitions (offering
// nothing while it's unconfigured, say) stays live here without rebuilding the agent.
public sealed class CompositeToolExecutor(IReadOnlyList<IOllamaToolExecutor> executors) : IOllamaToolExecutor
{
    public IReadOnlyList<OllamaToolDefinition> Definitions =>
        executors.SelectMany(executor => executor.Definitions).ToList();

    public Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken)
    {
        var owner = executors.FirstOrDefault(executor => executor.Definitions.Any(definition => definition.Name == call.Name));
        return owner?.ExecuteAsync(call, cancellationToken)
            ?? Task.FromResult(JsonSerializer.Serialize(new { error = $"Unknown tool: {call.Name}" }));
    }
}
