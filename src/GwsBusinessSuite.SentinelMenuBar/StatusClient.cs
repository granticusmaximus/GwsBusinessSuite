using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.SentinelMenuBar;

// GET /api/v1/sentinel/status with the stored key. Mirrors the server's DeveloperApiStatusSummary
// (camelCase JSON); fields the server adds later are simply ignored here.
internal sealed record StatusSummary(
    string Health,
    int UnreadHealthAlerts,
    int OpenTickets,
    int OverdueTickets,
    int DueFollowUps,
    int UnreadFormSubmissions,
    int PendingComments,
    int UnreadMessages,
    int UnreadAreaAlerts,
    StatusBooking? NextBooking,
    StatusTimer? RunningTimer,
    int AttentionCount,
    DateTimeOffset GeneratedAt);

internal sealed record StatusBooking(string Title, string AttendeeName, DateTimeOffset StartsAt);

internal sealed record StatusTimer(string ContactName, string Description, DateTimeOffset StartedAt);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StatusSummary))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;

internal enum StatusProblem
{
    None,
    NoKey,
    KeyRejected,
    Unreachable
}

internal sealed class StatusClient(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };

    public async Task<(StatusSummary? Summary, StatusProblem Problem)> FetchAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (null, StatusProblem.NoKey);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/sentinel/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, StatusProblem.KeyRejected);
            if (!response.IsSuccessStatusCode) return (null, StatusProblem.Unreachable);
            var summary = await response.Content.ReadFromJsonAsync(StatusJsonContext.Default.StatusSummary, cancellationToken);
            return summary is null ? (null, StatusProblem.Unreachable) : (summary, StatusProblem.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, StatusProblem.Unreachable);
        }
    }
}
