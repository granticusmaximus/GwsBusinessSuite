using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Wiki;

// Sits beside SentinelAiService rather than in Infrastructure for the same reason that one
// does: its only dependencies are abstractions, so it stays testable without a database.
public sealed class SentinelWritingAssistantService(
    IOllamaService ollama,
    ISiteSettingsService siteSettings,
    ILogger<SentinelWritingAssistantService>? logger = null) : ISentinelWritingAssistant
{
    // Deliberately generous. A local model that has just been paged in can take tens of
    // seconds to answer its first request, and an aggressive ceiling here would fail exactly
    // the way the Civic Watch AI overview did - silently, every time, for the cold call only.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<SentinelWritingResult> TransformAsync(
        string actionKey,
        string selectedText,
        CancellationToken cancellationToken = default)
    {
        var action = SentinelWritingActions.Find(actionKey);
        if (action is null)
        {
            // An unknown key means the editor and the catalog have drifted apart. Say so
            // plainly rather than falling back to some default transformation the user did
            // not ask for and would not be able to explain afterwards.
            return SentinelWritingResult.Failed("That writing action is not available.");
        }

        if (string.IsNullOrWhiteSpace(selectedText))
        {
            return SentinelWritingResult.Failed("Select some text first.");
        }

        var trimmed = selectedText.Trim();
        if (trimmed.Length > SentinelWritingAssistant.MaxSelectionLength)
        {
            return SentinelWritingResult.Failed(
                $"That selection is too long. Select at most {SentinelWritingAssistant.MaxSelectionLength:N0} characters.");
        }

        var settings = await siteSettings.GetSettingsAsync(cancellationToken);
        var model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride)
            ? SentinelGptDefaults.Model
            : settings.OllamaModelOverride;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);

        string raw;
        try
        {
            raw = await ollama.GenerateAsync(
                model,
                SentinelWritingAssistant.SystemPrompt,
                SentinelWritingAssistant.BuildUserPrompt(action, trimmed),
                timeoutSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user navigated away or closed the page - not a failure worth reporting back.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning("Sentinel writing action {Action} timed out against model {Model}.", action.Key, model);
            return SentinelWritingResult.Failed("The model took too long to respond. Try a shorter selection.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sentinel writing action {Action} failed against model {Model}.", action.Key, model);
            return SentinelWritingResult.Failed("The writing assistant is unavailable right now.");
        }

        var cleaned = SentinelWritingAssistant.CleanResponse(raw, action, trimmed);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            // CleanResponse returns empty both for an empty answer and for one that ran away
            // past the length ceiling. Either way there is nothing safe to put in the block.
            logger?.LogInformation(
                "Sentinel writing action {Action} produced no usable text ({Length} raw characters).",
                action.Key,
                raw?.Length ?? 0);
            return SentinelWritingResult.Failed("The model did not return usable text. Try again.");
        }

        return SentinelWritingResult.Ok(cleaned);
    }
}
