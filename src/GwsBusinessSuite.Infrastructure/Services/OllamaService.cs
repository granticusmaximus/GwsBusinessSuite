using GwsBusinessSuite.Application.Abstractions;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class OllamaService(
    HttpClient http,
    OllamaWorkloadScheduler workloadScheduler,
    OllamaPerformanceTracker performanceTracker,
    ILogger<OllamaService> logger) : IOllamaService
{
    private const string ModelKeepAlive = "30m";

    public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            stream = false,
            system = systemPrompt,
            prompt = userPrompt,
            keep_alive = ModelKeepAlive
        };

        try
        {
            var workloadPriority = workloadScheduler.CurrentPriority;
            var queueTimer = Stopwatch.StartNew();
            await using var workloadLease = await workloadScheduler.AcquireAsync(ct);
            queueTimer.Stop();
            LogQueueWait(model, queueTimer.Elapsed);
            var stopwatch = Stopwatch.StartNew();
            using var response = await http.PostAsJsonAsync("/api/generate", payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
            stopwatch.Stop();
            LogGenerationMetrics(
                model, result, queueTimer.Elapsed, stopwatch.Elapsed, stopwatch.Elapsed,
                workloadPriority, recordInteractiveChatPerformance: false);
            return result?.Response ?? string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Ollama generate request failed for model '{Model}'.", model);
            throw;
        }
    }

    public async Task WarmModelAsync(string model, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            stream = false,
            keep_alive = ModelKeepAlive
        };

        var queueTimer = Stopwatch.StartNew();
        await using var workloadLease = await workloadScheduler.AcquireAsync(ct);
        queueTimer.Stop();
        LogQueueWait(model, queueTimer.Elapsed);

        var stopwatch = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync("/api/generate", payload, ct);
        response.EnsureSuccessStatusCode();
        stopwatch.Stop();
        logger.LogInformation(
            "Warmed Ollama model '{Model}' in {WarmupMs:F0} ms and will retain it for {KeepAlive}.",
            model,
            stopwatch.Elapsed.TotalMilliseconds,
            ModelKeepAlive);
    }

    // NDJSON: Ollama writes one JSON object per line as generation progresses, with a final
    // {"done":true} line. HttpCompletionOption.ResponseHeadersRead is required so the body is
    // read incrementally rather than buffered whole before this method can start yielding.
    public IAsyncEnumerable<string> GenerateStreamAsync(
        string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        GenerateStreamCoreAsync(model, systemPrompt, userPrompt, null, ct);

    public IAsyncEnumerable<string> GenerateStreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken ct = default)
    {
        if (maxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        return GenerateStreamCoreAsync(model, systemPrompt, userPrompt, maxOutputTokens, ct);
    }

    private async IAsyncEnumerable<string> GenerateStreamCoreAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int? maxOutputTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        object payload = maxOutputTokens is { } outputLimit
            ? new
            {
                model,
                stream = true,
                system = systemPrompt,
                prompt = userPrompt,
                keep_alive = ModelKeepAlive,
                options = new { num_predict = outputLimit }
            }
            : new
            {
                model,
                stream = true,
                system = systemPrompt,
                prompt = userPrompt,
                keep_alive = ModelKeepAlive
            };

        var workloadPriority = workloadScheduler.CurrentPriority;
        var queueTimer = Stopwatch.StartNew();
        await using var workloadLease = await workloadScheduler.AcquireAsync(ct);
        queueTimer.Stop();
        LogQueueWait(model, queueTimer.Elapsed);
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? timeToFirstToken = null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate") { Content = JsonContent.Create(payload) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                logger.LogWarning(
                    "Ollama stream for model '{Model}' ended without a completion record after {ElapsedMs:F0} ms.",
                    model,
                    stopwatch.Elapsed.TotalMilliseconds);
                yield break;
            }
            if (line.Length == 0) continue;

            OllamaGenerateResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipped an unparseable Ollama stream line for model '{Model}'.", model);
                continue;
            }

            if (chunk is null) continue;
            if (!string.IsNullOrEmpty(chunk.Response))
            {
                timeToFirstToken ??= stopwatch.Elapsed;
                yield return chunk.Response;
            }
            if (chunk.Done)
            {
                stopwatch.Stop();
                LogGenerationMetrics(
                    model, chunk, queueTimer.Elapsed, stopwatch.Elapsed, timeToFirstToken,
                    workloadPriority, recordInteractiveChatPerformance: true);
                yield break;
            }
        }
    }

    public async Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("/api/tags", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: ct);
            return result?.Models?
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Ollama list-models request failed.");
            throw;
        }
    }

    // Ollama's /api/generate auto-detects an image-generation-capable model and returns
    // a base64 PNG in the response's "image" field instead of (or alongside) the usual
    // text "response" field - see Ollama's "Image Generation (Experimental)" docs. Kept
    // as its own response DTO rather than reusing OllamaGenerateResponse so text and
    // image responses aren't conflated.
    public async Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default)
    {
        var payload = new { model, stream = false, prompt };

        try
        {
            var queueTimer = Stopwatch.StartNew();
            await using var workloadLease = await workloadScheduler.AcquireAsync(ct);
            queueTimer.Stop();
            LogQueueWait(model, queueTimer.Elapsed);
            using var response = await http.PostAsJsonAsync("/api/generate", payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaImageGenerateResponse>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(result?.Image))
            {
                throw new InvalidOperationException(
                    $"Ollama returned no image data for model '{model}'. Confirm it's an installed model with image-generation capability.");
            }

            return result.Image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Ollama image generation request failed for model '{Model}'.", model);
            throw;
        }
    }

    public async Task PullModelAsync(string model, CancellationToken ct = default)
    {
        var payload = new { model, stream = false };

        try
        {
            using var response = await http.PostAsJsonAsync("/api/pull", payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaStatusResponse>(cancellationToken: ct);
            if (!string.Equals(result?.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Ollama did not report success pulling '{model}' (status: {result?.Status ?? "unknown"}).");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Ollama pull request failed for model '{Model}'.", model);
            throw;
        }
    }

    public async Task DeleteModelAsync(string model, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/delete")
            {
                Content = JsonContent.Create(new { model })
            };
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Ollama delete request failed for model '{Model}'.", model);
            throw;
        }
    }

    private sealed record OllamaStatusResponse(string? Status);

    private sealed record OllamaImageGenerateResponse([property: JsonPropertyName("image")] string? Image);

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("done")] bool Done = false,
        [property: JsonPropertyName("total_duration")] long? TotalDuration = null,
        [property: JsonPropertyName("load_duration")] long? LoadDuration = null,
        [property: JsonPropertyName("prompt_eval_count")] long? PromptEvalCount = null,
        [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration = null,
        [property: JsonPropertyName("eval_count")] long? EvalCount = null,
        [property: JsonPropertyName("eval_duration")] long? EvalDuration = null);

    private sealed record OllamaTagsResponse([property: JsonPropertyName("models")] OllamaTagModel[]? Models);

    private sealed record OllamaTagModel([property: JsonPropertyName("name")] string Name);

    private void LogGenerationMetrics(
        string model,
        OllamaGenerateResponse? result,
        TimeSpan queueWait,
        TimeSpan requestDuration,
        TimeSpan? timeToFirstToken,
        OllamaWorkloadPriority workloadPriority,
        bool recordInteractiveChatPerformance)
    {
        var tokensPerSecond = result?.EvalCount is > 0 && result.EvalDuration is > 0
            ? result.EvalCount.Value / (result.EvalDuration.Value / 1_000_000_000d)
            : 0d;
        logger.LogInformation(
            "Ollama generation metrics for '{Model}': request {RequestMs:F0} ms, first token {FirstTokenMs:F0} ms, " +
            "model load {LoadMs:F0} ms, prompt {PromptTokens} tokens in {PromptMs:F0} ms, " +
            "output {OutputTokens} tokens at {TokensPerSecond:F1} tokens/sec.",
            model,
            requestDuration.TotalMilliseconds,
            timeToFirstToken?.TotalMilliseconds ?? requestDuration.TotalMilliseconds,
            NanosecondsToMilliseconds(result?.LoadDuration),
            result?.PromptEvalCount ?? 0,
            NanosecondsToMilliseconds(result?.PromptEvalDuration),
            result?.EvalCount ?? 0,
            tokensPerSecond);
        if (recordInteractiveChatPerformance
            && workloadPriority == OllamaWorkloadPriority.Interactive)
        {
            performanceTracker.Record(new OllamaPerformanceSnapshot(
                model,
                DateTimeOffset.UtcNow,
                queueWait.TotalMilliseconds,
                requestDuration.TotalMilliseconds,
                timeToFirstToken?.TotalMilliseconds ?? requestDuration.TotalMilliseconds,
                NanosecondsToMilliseconds(result?.LoadDuration),
                result?.PromptEvalCount ?? 0,
                NanosecondsToMilliseconds(result?.PromptEvalDuration),
                result?.EvalCount ?? 0,
                tokensPerSecond));
        }
    }

    private static double NanosecondsToMilliseconds(long? nanoseconds) =>
        (nanoseconds ?? 0) / 1_000_000d;

    private void LogQueueWait(string model, TimeSpan queueWait)
    {
        if (queueWait >= TimeSpan.FromMilliseconds(25))
        {
            logger.LogInformation(
                "Ollama model '{Model}' waited {QueueWaitMs:F0} ms for the local generation slot.",
                model,
                queueWait.TotalMilliseconds);
        }
    }
}
