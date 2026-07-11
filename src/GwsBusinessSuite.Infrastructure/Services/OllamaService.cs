using GwsBusinessSuite.Application.Abstractions;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class OllamaService(HttpClient http, ILogger<OllamaService> logger) : IOllamaService
{
    public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            stream = false,
            system = systemPrompt,
            prompt = userPrompt
        };

        try
        {
            using var response = await http.PostAsJsonAsync("/api/generate", payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
            return result?.Response ?? string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Ollama generate request failed for model '{Model}'.", model);
            throw;
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

    private sealed record OllamaGenerateResponse(string Response);

    private sealed record OllamaTagsResponse([property: JsonPropertyName("models")] OllamaTagModel[]? Models);

    private sealed record OllamaTagModel([property: JsonPropertyName("name")] string Name);
}
