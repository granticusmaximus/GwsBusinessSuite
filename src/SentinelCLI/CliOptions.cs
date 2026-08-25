namespace GwsBusinessSuite.SentinelCLI;

public enum CliMode
{
    Agent,
    ModelsList,
    ModelsSync,
    Doctor,
    Help
}

public sealed record CliOptions(
    CliMode Mode,
    string WorkspaceRoot,
    string Model,
    Uri OllamaBaseUri,
    string Prompt,
    bool ReadOnly,
    bool AutoApprove,
    int MaxRounds)
{
    public const string DefaultModel = "qwen2.5-coder";
    public const int DefaultMaxRounds = 12;

    public bool IsInteractive => Mode == CliMode.Agent && string.IsNullOrWhiteSpace(Prompt);

    public static CliOptions Parse(IReadOnlyList<string> args, string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var mode = CliMode.Agent;
        var workspace = currentDirectory;
        var model = Environment.GetEnvironmentVariable("SENTINELCLI_MODEL")
                    ?? Environment.GetEnvironmentVariable("SENTINELGPT_MODEL")
                    ?? DefaultModel;
        var ollama = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://127.0.0.1:11434";
        var readOnly = false;
        var autoApprove = false;
        var maxRounds = DefaultMaxRounds;
        var promptParts = new List<string>();

        var index = 0;
        if (args.Count > 0)
        {
            switch (args[0])
            {
                case "models":
                    if (args.Count < 2)
                        throw new CliUsageException("Choose 'models list' or 'models sync'.");
                    mode = args[1] switch
                    {
                        "list" => CliMode.ModelsList,
                        "sync" => CliMode.ModelsSync,
                        _ => throw new CliUsageException("Choose 'models list' or 'models sync'.")
                    };
                    index = 2;
                    break;
                case "doctor":
                    mode = CliMode.Doctor;
                    index = 1;
                    break;
                case "help" or "--help" or "-h":
                    mode = CliMode.Help;
                    index = 1;
                    break;
                case "chat":
                    index = 1;
                    break;
            }
        }

        while (index < args.Count)
        {
            var arg = args[index++];
            switch (arg)
            {
                case "--repo" or "-C":
                    workspace = RequireValue(args, ref index, arg);
                    break;
                case "--model" or "-m":
                    model = RequireValue(args, ref index, arg);
                    break;
                case "--ollama-url":
                    ollama = RequireValue(args, ref index, arg);
                    break;
                case "--read-only":
                    readOnly = true;
                    break;
                case "--yes" or "-y":
                    autoApprove = true;
                    break;
                case "--max-rounds":
                    var rawRounds = RequireValue(args, ref index, arg);
                    if (!int.TryParse(rawRounds, out maxRounds) || maxRounds is < 1 or > 30)
                        throw new CliUsageException("--max-rounds must be between 1 and 30.");
                    break;
                case "--help" or "-h":
                    mode = CliMode.Help;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        throw new CliUsageException($"Unknown option: {arg}");
                    promptParts.Add(arg);
                    break;
            }
        }

        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root))
            throw new CliUsageException($"Workspace directory does not exist: {root}");
        if (!IsSafeModelName(model))
            throw new CliUsageException("The model name contains unsupported characters.");

        var baseUri = NormalizeOllamaUri(ollama);
        if (!baseUri.IsLoopback)
            throw new CliUsageException("SentinelCLI only connects to a loopback Ollama server.");

        return new CliOptions(
            mode,
            root,
            model,
            baseUri,
            string.Join(' ', promptParts).Trim(),
            readOnly,
            autoApprove,
            maxRounds);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            throw new CliUsageException($"{option} requires a value.");
        return args[index++];
    }

    private static bool IsSafeModelName(string model) =>
        !string.IsNullOrWhiteSpace(model)
        && model.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/');

    private static Uri NormalizeOllamaUri(string value)
    {
        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = $"http://{normalized}";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new CliUsageException("--ollama-url must be an HTTP or HTTPS URL.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
    }
}

public sealed class CliUsageException(string message) : Exception(message);
