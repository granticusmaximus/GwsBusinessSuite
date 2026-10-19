using System.Text.Json;

namespace GwsBusinessSuite.Application.Automation;

// The Code step (core.code): a short JavaScript function body run against the current item.
// It runs in a locked-down interpreter (see JintAutomationCodeRunner): no access to .NET, files,
// the network or timers, and hard limits on time, memory, statements and recursion.
//
//   $json           this step's input
//   $node("Name")   an earlier step's output
//   console.log()   lines shown in the run's output under "_logs"
//
// Return an object (the step's output), an array (one output item per element, for splitting a
// list), or any other value (output as { "result": value }). Returning nothing passes the input on.
public static class AutomationCodeStep
{
    public const string TypeKey = "core.code";
    public const int MaxCodeLength = 20_000;
    public const int MaxItems = 1_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    public const long MemoryLimitBytes = 32 * 1024 * 1024;
    public const int MaxStatements = 2_000_000;
}

public interface IAutomationCodeRunner
{
    // The output items. Throws InvalidOperationException with a readable message on any error
    // (syntax, a thrown error, or a limit hit).
    Task<IReadOnlyList<JsonElement>> RunAsync(string code, JsonElement input, IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName, CancellationToken cancellationToken = default);
}
