using System.Text;

namespace GwsBusinessSuite.OllamaKit;

// A parsed Modelfile, ready to POST to /api/create.
//
// Ollama's create endpoint used to accept a whole Modelfile as one "modelfile" string; that
// field was removed (0.33.3 answers HTTP 400 for it) in favour of structured fields, so anything
// that wants to build a profile over HTTP has to do this splitting itself. Doing it here keeps
// ollama/SentinelGPT.Modelfile the single source of truth for the profile - the CLI can still
// hand the same text to `ollama create -f`, while a sandboxed host that cannot spawn a process
// at all builds the identical model through the API.
public sealed record OllamaModelfile(
    string From,
    string? System,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<OllamaModelfileMessage> Messages);

public sealed record OllamaModelfileMessage(string Role, string Content);

public static class OllamaModelfileParser
{
    // Deliberately handles only the directives this repo's profile actually uses (FROM,
    // PARAMETER, SYSTEM, MESSAGE) rather than pretending to be a general Modelfile parser -
    // an unrecognized directive is ignored rather than silently mistranslated into something
    // the API would apply differently.
    public static OllamaModelfile Parse(string modelfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelfile);

        string? from = null;
        string? system = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<OllamaModelfileMessage>();

        var lines = modelfile.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var (directive, rest) = SplitDirective(line);
            switch (directive)
            {
                case "FROM":
                    from = rest;
                    break;
                case "PARAMETER":
                    var (key, value) = SplitDirective(rest);
                    if (key.Length > 0 && value.Length > 0)
                        parameters[key] = value;
                    break;
                case "SYSTEM":
                    system = ReadPossiblyTripleQuoted(lines, rest, ref i);
                    break;
                case "MESSAGE":
                    var (role, content) = SplitDirective(rest);
                    if (role.Length > 0 && content.Length > 0)
                        messages.Add(new OllamaModelfileMessage(role.ToLowerInvariant(), Unquote(content)));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("A Modelfile must declare a FROM base model.");
        return new OllamaModelfile(from, system, parameters, messages);
    }

    private static (string Head, string Remainder) SplitDirective(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    // SYSTEM is the one directive here that routinely spans lines, delimited by """. A
    // single-line SYSTEM (quoted or bare) is also valid and stays on its own line.
    private static string ReadPossiblyTripleQuoted(string[] lines, string rest, ref int index)
    {
        const string fence = "\"\"\"";
        if (!rest.StartsWith(fence, StringComparison.Ordinal))
            return Unquote(rest);

        var firstLine = rest[fence.Length..];
        // A whole block opened and closed on the same line.
        if (firstLine.EndsWith(fence, StringComparison.Ordinal))
            return firstLine[..^fence.Length].Trim();

        var builder = new StringBuilder();
        if (firstLine.Length > 0) builder.AppendLine(firstLine);
        for (index++; index < lines.Length; index++)
        {
            var line = lines[index];
            var closing = line.IndexOf(fence, StringComparison.Ordinal);
            if (closing >= 0)
            {
                if (closing > 0) builder.AppendLine(line[..closing]);
                break;
            }
            builder.AppendLine(line);
        }
        return builder.ToString().Trim();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
