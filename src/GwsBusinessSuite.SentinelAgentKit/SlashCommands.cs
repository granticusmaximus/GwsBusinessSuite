namespace GwsBusinessSuite.SentinelAgentKit;

public sealed record SlashCommand(string Name, string Usage, string Description);

// SentinelCLI's session commands, described once so the console tool and the Mac app offer the
// same vocabulary. This is what "interacting with SentinelCLI" means inside the sandboxed app:
// App Sandbox denies process execution outright, so the app can never shell out to the
// sentinelcli binary - but the commands it answers to are just an interaction model, and that
// ports perfectly. Typing /skills does in the chat what it does in the terminal.
//
// Parsing lives here rather than in the page so both hosts agree on the edge cases (bare "/",
// unknown names, arguments containing further slashes) instead of each inventing its own.
public static class SlashCommands
{
    public const string Help = "help";
    public const string Skills = "skills";
    public const string Agent = "agent";
    public const string Models = "models";
    public const string Clear = "clear";
    public const string Workspace = "workspace";

    public static IReadOnlyList<SlashCommand> All { get; } =
    [
        new(Help, "/help", "List these commands."),
        new(Skills, "/skills [name] [request]", "List your skills, or apply one to a single message."),
        new(Agent, "/agent [name]", "List personas, or switch to one."),
        new(Models, "/models", "Show which local models are installed and which can use tools."),
        new(Workspace, "/workspace", "Show the attached project folder, if any."),
        new(Clear, "/clear", "Start a new conversation.")
    ];

    // True only for something that really is a command attempt. Three conditions, and the third
    // matters more than it looks: this is a developer's assistant, so messages that open with a
    // filesystem path are entirely normal ("/app/data is full, what should I prune?"). Requiring
    // the first token to be a bare word - no second slash, letters and hyphens only - keeps
    // those as ordinary prompts instead of answering them with "unknown command".
    //
    // Deliberately not restricted to *known* names: a near miss like "/skill" should be caught
    // and corrected, not silently forwarded to the model, which is the failure this whole
    // feature exists to fix.
    public static bool LooksLikeCommand(string? input)
    {
        var trimmed = input?.Trim();
        if (trimmed is not { Length: > 1 } || trimmed[0] != '/') return false;

        var space = trimmed.IndexOf(' ');
        var token = space < 0 ? trimmed[1..] : trimmed[1..space];
        return token.Length > 0
            && char.IsLetter(token[0])
            && token.All(character => char.IsLetter(character) || character == '-');
    }

    // For an unrecognized name, the closest thing we can honestly offer: a command that starts
    // with what was typed, or that what was typed starts with. Turns "/skill" into a pointer to
    // "/skills" rather than a flat rejection.
    public static SlashCommand? SuggestFor(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(command =>
                command.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(command.Name, StringComparison.OrdinalIgnoreCase));

    // Splits "/skills review this file" into ("skills", "review this file"). The name is
    // lower-cased so /Skills works; the argument keeps its original casing and inner spacing
    // because it is usually a prompt destined for the model.
    public static bool TryParse(string? input, out string name, out string argument)
    {
        name = string.Empty;
        argument = string.Empty;
        if (!LooksLikeCommand(input)) return false;

        var trimmed = input!.Trim();
        var space = trimmed.IndexOf(' ');
        name = (space < 0 ? trimmed[1..] : trimmed[1..space]).ToLowerInvariant();
        argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        return name.Length > 0;
    }

    public static SlashCommand? Find(string name) =>
        All.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string BuildHelp()
    {
        var width = All.Max(command => command.Usage.Length);
        var lines = All.Select(command => $"  {command.Usage.PadRight(width)}   {command.Description}");
        return "Commands (the same ones SentinelCLI answers to):\n"
            + string.Join('\n', lines)
            + "\n\nAnything not starting with / is sent to the model as a normal message.";
    }
}
