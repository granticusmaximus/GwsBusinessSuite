using GwsBusinessSuite.Application.Abstractions;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class OllamaService(HttpClient http) : IOllamaService
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

        using var response = await http.PostAsJsonAsync("/api/generate", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
        return result?.Response ?? string.Empty;
    }

    public async Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default)
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

    private sealed record OllamaGenerateResponse(string Response);

    private sealed record OllamaTagsResponse([property: JsonPropertyName("models")] OllamaTagModel[]? Models);

    private sealed record OllamaTagModel([property: JsonPropertyName("name")] string Name);
}
