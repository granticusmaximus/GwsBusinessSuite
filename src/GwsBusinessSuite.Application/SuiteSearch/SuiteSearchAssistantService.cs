using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.SuiteSearch;

// Turns a plain-language request into one find_records call (Ollama's native tool calling, on the
// same model as SentinelGPT) and runs it as a structured search. Anything that goes wrong - no
// tool call, an unknown kind, Ollama busy or down - is "no answer", never an error the palette
// has to show; the keyword results are already there.
public sealed class SuiteSearchAssistantService(
    IOllamaService ollama,
    ISiteSettingsService siteSettings,
    ISuiteSearchService search,
    TimeProvider time,
    ILogger<SuiteSearchAssistantService> logger) : ISuiteSearchAssistant
{
    private static readonly IReadOnlyList<OllamaToolDefinition> Tools =
        [new OllamaToolDefinition(SuiteSearchRequestRules.ToolName, SuiteSearchRequestRules.ToolDescription, SuiteSearchRequestRules.ToolSchema)];

    public async Task<SuiteSearchAnswer?> AskAsync(string question, string performedBy, CancellationToken ct = default)
    {
        question = (question ?? string.Empty).Trim();
        if (!SuiteSearchRequestRules.LooksLikeRequest(question)) return null;
        try
        {
            var settings = await siteSettings.GetSettingsAsync(ct);
            var model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? SentinelGptDefaults.Model : settings.OllamaModelOverride;
            var messages = new List<OllamaChatMessage>
            {
                new("system", SystemPrompt(time.GetLocalNow())),
                new("user", question)
            };
            var response = await ollama.ChatAsync(model, messages, Tools, ct);
            var call = response.ToolCalls.FirstOrDefault(item => item.Name == SuiteSearchRequestRules.ToolName);
            var request = SuiteSearchRequestRules.FromToolArguments(call?.ArgumentsJson);
            logger.LogInformation("⌘K request {Question} became {Arguments}, read as {Request}.", question, call?.ArgumentsJson ?? "(no tool call)", request);
            if (request is null) return null;
            request = SuiteSearchRequestRules.FillFromQuestion(request, question);
            // A name read from the request narrows to that contact if one exists; otherwise the
            // search runs without it rather than finding nothing.
            if (SuiteSearchRequestRules.OwnerFromQuestion(request, question) is { } owner)
            {
                var owned = await search.FindAsync(request with { Contact = owner }, ct);
                if (owned.Count > 0) return new SuiteSearchAnswer(request.Summary, owned);
            }
            return new SuiteSearchAnswer(request.Summary, await search.FindAsync(request, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "SentinelGPT couldn't turn a ⌘K request into a search.");
            return null;
        }
    }

    private static string SystemPrompt(DateTimeOffset now) => $"""
        You turn an admin's request into one search of their business records by calling {SuiteSearchRequestRules.ToolName} exactly once.
        Today is {now:dddd d MMMM yyyy}.
        Use status only for a status the request names or clearly means (an unpaid invoice is Sent; an open ticket is Open; leads are Lead; customers are Customer).
        Use overdue for anything past due, late or behind, and for contacts someone needs to follow up with. Use within_days for time words: today 1, this week 7, this month 30.
        Use priority for urgent, high, normal or low tickets; urgent is a priority, not overdue.
        Put a person's name in contact only when finding tickets, invoices or deals; when finding contacts, put the name in text.
        Leave text empty unless the request names words to match. Keep summary short and plain.
        """;
}
