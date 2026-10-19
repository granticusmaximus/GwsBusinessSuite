using System.Text.Json;
using GwsBusinessSuite.Application.Automation;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace GwsBusinessSuite.Infrastructure.Services;

// Runs the Code step in Jint, a JavaScript interpreter written in .NET. Nothing from .NET is
// exposed (no AllowClr), so a script can only work with the data it's given; time, memory,
// statement and recursion limits stop runaway code. Each run gets a fresh engine.
public sealed class JintAutomationCodeRunner : IAutomationCodeRunner
{
    private const int MaxLogLines = 50;
    private const int MaxOutputChars = 2_000_000;

    public Task<IReadOnlyList<JsonElement>> RunAsync(string code, JsonElement input, IReadOnlyDictionary<string, JsonElement>? nodeOutputsByName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("The Code step has no code.");
        if (code.Length > AutomationCodeStep.MaxCodeLength)
            throw new InvalidOperationException($"The Code step's code is longer than {AutomationCodeStep.MaxCodeLength:N0} characters.");

        var logs = new List<string>();
        var userLines = code.Split('\n').Length;
        using var engine = new Engine(options => options
            .Strict()
            .TimeoutInterval(AutomationCodeStep.Timeout)
            .LimitMemory(AutomationCodeStep.MemoryLimitBytes)
            .MaxStatements(AutomationCodeStep.MaxStatements)
            .LimitRecursion(64)
            .RegexTimeoutInterval(TimeSpan.FromMilliseconds(500))
            .CancellationToken(cancellationToken));

        try
        {
            // Data goes in as JSON text and is parsed inside the script, so only plain values cross over.
            engine.SetValue("__input", input.ValueKind == JsonValueKind.Undefined ? "null" : input.GetRawText());
            engine.SetValue("__nodes", JsonSerializer.Serialize(nodeOutputsByName?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? []));
            engine.SetValue("__log", new Action<string>(line =>
            {
                if (logs.Count < MaxLogLines) logs.Add(line.Length > 1000 ? line[..1000] : line);
            }));
            var result = engine.Evaluate("""
                const $json = JSON.parse(__input);
                const __nodeData = JSON.parse(__nodes);
                const $node = (name) => Object.prototype.hasOwnProperty.call(__nodeData, name) ? __nodeData[name] : undefined;
                const console = { log: (...parts) => __log(parts.map(p => typeof p === 'string' ? p : JSON.stringify(p)).join(' ')) };
                const __result = (function () {
                """ + "\n" + code + "\n" + """
                })();
                __result === undefined ? undefined : JSON.stringify(__result);
                """);
            return Task.FromResult(ToItems(result, input, logs));
        }
        catch (JavaScriptException ex) when (ex.Error.IsObject() && ex.Error.AsObject().Get("name").ToString() == "SyntaxError")
        {
            // Positions in the message refer to the wrapper script; report the line in the user's code.
            var position = WrapperPositionPattern.Match(ex.Message);
            var message = WrapperPositionPattern.Replace(ex.Message, string.Empty).Trim();
            var line = position.Success ? int.Parse(position.Groups["line"].Value) : ex.Location.Start.Line;
            throw new InvalidOperationException($"Code step has a syntax error: {message}{SyntaxLine(line, userLines)}");
        }
        catch (JavaScriptException ex)
        {
            throw new InvalidOperationException($"Code step error: {ex.Message}{LineOf(ex.Location.Start.Line)}");
        }
        catch (Acornima.ParseErrorException ex)
        {
            throw new InvalidOperationException($"Code step has a syntax error: {ex.Error.Description}{LineOf(ex.Error.LineNumber)}");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Code step took longer than {AutomationCodeStep.Timeout.TotalSeconds:0} seconds and was stopped.");
        }
        catch (MemoryLimitExceededException)
        {
            throw new InvalidOperationException("Code step used too much memory and was stopped.");
        }
        catch (StatementsCountOverflowException)
        {
            throw new InvalidOperationException("Code step ran too many steps (an endless loop?) and was stopped.");
        }
        catch (RecursionDepthOverflowException)
        {
            throw new InvalidOperationException("Code step recursed too deeply and was stopped.");
        }
        catch (ExecutionCanceledException)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // The wrapper puts the user's code on line 6 of the evaluated script.
    private static string LineOf(int line) => line > 5 ? $" (line {line - 5})" : string.Empty;

    // A syntax error past the user's last line (e.g. a missing "}") is reported as at the end.
    private static string SyntaxLine(int wrapperLine, int userLines) =>
        wrapperLine - 5 > userLines ? " (at the end of your code)" : LineOf(wrapperLine);

    private static readonly System.Text.RegularExpressions.Regex WrapperPositionPattern =
        new(@"\(<anonymous>:(?<line>\d+):\d+\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static IReadOnlyList<JsonElement> ToItems(JsValue result, JsonElement input, List<string> logs)
    {
        if (result.IsUndefined()) return [WithLogs(input, logs)];
        var json = result.AsString();
        if (json.Length > MaxOutputChars) throw new InvalidOperationException("Code step returned more than 2 MB of data.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() > AutomationCodeStep.MaxItems)
                throw new InvalidOperationException($"Code step returned more than {AutomationCodeStep.MaxItems:N0} items.");
            var items = root.EnumerateArray().Select(item => WithLogs(item, logs)).ToList();
            return items.Count == 0 ? [] : items;
        }
        return [WithLogs(root, logs)];
    }

    // Objects stay as they are (plus _logs when anything was logged); other values become { result }.
    private static JsonElement WithLogs(JsonElement value, List<string> logs)
    {
        var node = value.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!.AsObject()
            : new System.Text.Json.Nodes.JsonObject { ["result"] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText()) };
        if (logs.Count > 0) node["_logs"] = new System.Text.Json.Nodes.JsonArray(logs.Select(line => (System.Text.Json.Nodes.JsonNode?)line).ToArray());
        using var document = JsonDocument.Parse(node.ToJsonString(Readable));
        return document.RootElement.Clone();
    }
}
