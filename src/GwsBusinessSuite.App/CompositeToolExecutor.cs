using System.Text.Json;
using GwsBusinessSuite.OllamaKit;

namespace GwsBusinessSuite.App;

// Unions two or more tool sources (e.g. NativeToolExecutor's wiki tools + WorkspaceTools' file
// tools) into one IOllamaToolExecutor, so SentinelGptPage can offer the model a single merged
// toolset instead of switching between mutually-exclusive executors behind a mode flag.
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
