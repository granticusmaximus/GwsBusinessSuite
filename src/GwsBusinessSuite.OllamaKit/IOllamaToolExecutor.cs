namespace GwsBusinessSuite.OllamaKit;

// Lets OllamaToolCallingAgent stay ignorant of what a "tool" actually does - a console coding
// agent, a native chat tab, and a future host can each supply their own Definitions/ExecuteAsync
// without the loop itself changing. Returning an empty Definitions list is a valid, safe state
// (no tools offered to the model at all), used deliberately when a host isn't ready to expose
// any tools yet.
public interface IOllamaToolExecutor
{
    IReadOnlyList<OllamaToolDefinition> Definitions { get; }

    Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken);
}
