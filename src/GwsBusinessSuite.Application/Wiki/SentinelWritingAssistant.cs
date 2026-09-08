namespace GwsBusinessSuite.Application.Wiki;

// Inline writing assistance for the Sentinel block editor's selection toolbar - the
// "select a sentence and press improve" surface, which is a different job from SentinelGPT
// (SentinelAiService). That one is a conversational agent with tools and memory; this one
// takes a span of text, applies exactly one named transformation, and returns replacement
// text. Nothing here is conversational and nothing here can call a tool.
//
// The action set is deliberately CLOSED. The editor sends an action key, never a prompt, so
// no text typed into a page can reach the system prompt and redirect the model. Selected text
// is passed as user content only, and the response is treated as untrusted plain text.
public sealed record SentinelWritingAction(
    string Key,
    string Label,
    string Icon,
    string Instruction,
    // Continue/expand legitimately grow the text; the rest must not run away with it, so they
    // are held near the original length. Enforced in the prompt, then again on the response.
    bool AllowsGrowth = false);

public static class SentinelWritingActions
{
    public static readonly IReadOnlyList<SentinelWritingAction> All =
    [
        new("improve", "Improve writing", "✨",
            "Rewrite the text so it reads more clearly and naturally. Preserve every fact, name, number, and link exactly as written."),
        new("shorten", "Make shorter", "✂️",
            "Rewrite the text more concisely. Remove redundancy only - never drop a fact, name, number, or link."),
        new("lengthen", "Make longer", "➕",
            "Expand the text with more detail drawn only from what it already says. Do not introduce any new fact, name, number, statistic, or citation.",
            AllowsGrowth: true),
        new("fix", "Fix spelling & grammar", "✓",
            "Correct spelling, grammar, and punctuation. Change nothing else - preserve the wording, tone, facts, and formatting of the original."),
        new("simplify", "Simplify language", "○",
            "Rewrite the text in plain language a general reader can follow. Keep every fact intact and do not add explanations that were not already there."),
        new("professional", "Make professional", "■",
            "Rewrite the text in a professional tone suitable for a work document. Keep the meaning and every fact unchanged."),
        new("continue", "Continue writing", "→",
            "Continue the text with one or two additional sentences in the same voice and tense. Do not repeat what is already written, and do not introduce facts, names, numbers, or citations that are not supported by the text.",
            AllowsGrowth: true)
    ];

    public static SentinelWritingAction? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(action => string.Equals(action.Key, key, StringComparison.OrdinalIgnoreCase));
}

public static class SentinelWritingAssistant
{
    // A selection larger than this is almost certainly a whole-page select-all rather than the
    // sentence-or-paragraph this feature is for. Rejecting it keeps a stray Ctrl+A from pushing
    // an entire document through the model and stalling the circuit.
    public const int MaxSelectionLength = 4_000;

    // Rewrites are allowed to grow somewhat - a "simplify" often does - but a model that starts
    // generating an essay from one sentence has gone off the rails, and pasting that into the
    // page destroys the block. Growth-allowing actions get more headroom, not unlimited.
    private const int LengthCeilingMultiplier = 3;
    private const int GrowthCeilingMultiplier = 6;
    private const int MinimumLengthCeiling = 400;

    public const string SystemPrompt =
        """
        You are a writing assistant embedded in a document editor. You transform a passage of
        text and return the transformed text.

        Rules, all mandatory:
        - Return ONLY the resulting text. No preamble, no explanation, no commentary.
        - Never wrap the result in quotation marks or code fences.
        - Never add a heading, a label, or a "Here is..." sentence.
        - Never invent facts. Do not add names, numbers, dates, statistics, URLs, or citations
          that are not present in the text you were given.
        - If the passage contains a fact you cannot verify, keep it exactly as written rather
          than correcting, embellishing, or removing it.
        - Preserve the language of the original.
        """;

    public static string BuildUserPrompt(SentinelWritingAction action, string selectedText)
    {
        ArgumentNullException.ThrowIfNull(action);

        // The instruction is a constant from the closed catalog above and the selection is
        // fenced off under its own heading, so a page containing text like "ignore previous
        // instructions" arrives plainly as content to be rewritten rather than as a directive.
        return $"""
            Task: {action.Instruction}

            Text:
            {selectedText}
            """;
    }

    // Models routinely ignore "return only the text": they add a lead-in sentence, fence the
    // result, or quote it. Stripping that here is what makes the result safe to drop straight
    // into a block without the user having to clean it up by hand.
    public static string CleanResponse(string? response, SentinelWritingAction action, string originalText)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (string.IsNullOrWhiteSpace(response))
        {
            return string.Empty;
        }

        // Order matters: a reasoning model's <think> block can itself contain fences, quotes
        // and "Here is..." lines, so it has to come off before anything else looks at the text.
        var text = response.Replace("\r\n", "\n").Trim();
        text = StripThinkBlock(text);
        text = StripCodeFence(text);
        text = StripLeadIn(text);
        text = StripWrappingQuotes(text);
        text = text.Trim();

        var ceiling = Math.Max(
            MinimumLengthCeiling,
            originalText.Length * (action.AllowsGrowth ? GrowthCeilingMultiplier : LengthCeilingMultiplier));

        // Truncating mid-sentence would be worse than returning nothing: the user would paste a
        // fragment and not notice. Treat a runaway response as a failure instead.
        return text.Length > ceiling ? string.Empty : text;
    }

    private static string StripThinkBlock(string text)
    {
        // Reasoning models (deepseek-r1 and friends) emit <think>...</think> even with
        // think:false on some builds. Anything before the closing tag is not the answer.
        var closing = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return closing >= 0 ? text[(closing + "</think>".Length)..].Trim() : text;
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }

        var body = text[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }

    private static string StripLeadIn(string text)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return text;
        }

        var firstLine = text[..newline].TrimEnd();
        if (!firstLine.EndsWith(':') || firstLine.Length > 80)
        {
            return text;
        }

        // Only drop a trailing-colon first line when it actually reads as a lead-in. A genuine
        // rewrite can legitimately open with a short line ending in a colon (a list header, for
        // instance), and eating that would silently lose the user's content.
        var leadInMarkers = new[] { "here", "sure", "certainly", "rewritten", "revised", "result", "output", "corrected" };
        var lowered = firstLine.ToLowerInvariant();
        return leadInMarkers.Any(marker => lowered.StartsWith(marker, StringComparison.Ordinal))
            ? text[(newline + 1)..].Trim()
            : text;
    }

    private static string StripWrappingQuotes(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        var first = text[0];
        var last = text[^1];
        var isPlainQuoted = (first == '"' && last == '"') || (first == '“' && last == '”');

        // Only unwrap when the quotes are the outermost pair, otherwise a passage that merely
        // opens and closes with quoted speech would lose both of its real quotation marks.
        return isPlainQuoted && text.IndexOf(first, 1) == text.Length - 1
            ? text[1..^1].Trim()
            : text;
    }
}

// What the editor's menu is built from. Deliberately narrower than SentinelWritingAction:
// the instruction text is prompt material and has no business crossing to the browser, where
// it would only invite someone to try posting a modified one back.
public sealed record SentinelWritingActionOption(string Key, string Label, string Icon);

public sealed record SentinelWritingResult(bool Succeeded, string Text, string? Error)
{
    public static SentinelWritingResult Ok(string text) => new(true, text, null);
    public static SentinelWritingResult Failed(string error) => new(false, string.Empty, error);
}

public interface ISentinelWritingAssistant
{
    Task<SentinelWritingResult> TransformAsync(
        string actionKey,
        string selectedText,
        CancellationToken cancellationToken = default);
}
