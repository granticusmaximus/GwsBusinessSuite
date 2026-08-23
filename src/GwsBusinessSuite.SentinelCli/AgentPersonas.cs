namespace GwsBusinessSuite.SentinelCli;

// Personas shape tone and priorities via an extra system-prompt paragraph - they are advisory,
// not enforcement. Unlike /plan (which actually removes mutation tools via WorkspaceTools),
// a persona cannot restrict what the model is allowed to do; "reviewer" asks the model not to
// propose edits, it doesn't take replace_in_file/write_file/run_command away. Combine /agent
// reviewer with /plan (or --read-only) for an enforced read-only review.
public sealed record AgentPersona(string Name, string Description, string Instructions);

public static class AgentPersonas
{
    public static readonly AgentPersona Default = new(
        "coder", "General coding and analysis (default)", "");

    public static readonly AgentPersona Reviewer = new(
        "reviewer", "Read-only code review",
        "Focus on correctness risks, security, and maintainability. Do not propose edits even if " +
        "mutation tools are available; describe what you'd change in prose instead.");

    public static readonly AgentPersona TestWriter = new(
        "test-writer", "Adds or strengthens test coverage",
        "Prioritize test coverage for the request. Prefer small, focused tests over broad rewrites. " +
        "Match this repository's existing test conventions rather than introducing a new style.");

    public static readonly AgentPersona DocsWriter = new(
        "docs-writer", "Writes or updates documentation",
        "Prioritize clear, accurate documentation for the request. Match the tone and structure of " +
        "this repository's existing docs rather than introducing a new style.");

    public static readonly IReadOnlyList<AgentPersona> All = [Default, Reviewer, TestWriter, DocsWriter];

    public static AgentPersona? Find(string name) =>
        All.FirstOrDefault(persona => string.Equals(persona.Name, name, StringComparison.OrdinalIgnoreCase));
}
