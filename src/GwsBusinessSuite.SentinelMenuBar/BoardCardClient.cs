using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwsBusinessSuite.SentinelMenuBar;

// Add a Card (Boards K8d): a one-line card ("Fix login !high #bug fri") to a board, through
// POST /api/v1/boards/cards with a key that has the boards:write scope. A card typed while the
// server can't be reached waits in CardOutbox and goes on the next successful refresh.
internal sealed record BoardChoice(Guid Id, string Title, string? Icon, int OpenCards);
internal sealed record CardRequest(Guid? BoardId, string Text);
internal sealed record CardResult(Guid BoardId, string BoardTitle, Guid CardId, string Title, string Column, string Url);
internal sealed record QueuedCard(Guid? BoardId, string Text, DateTimeOffset QueuedAt);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<BoardChoice>))]
[JsonSerializable(typeof(CardRequest))]
[JsonSerializable(typeof(CardResult))]
[JsonSerializable(typeof(List<QueuedCard>))]
internal sealed partial class BoardJsonContext : JsonSerializerContext;

internal enum CardProblem
{
    None,
    NoKey,
    // The key works but lacks boards:write.
    NotAllowed,
    Unreachable,
    Refused
}

internal sealed class BoardCardClient(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };

    public async Task<(IReadOnlyList<BoardChoice> Boards, CardProblem Problem)> ListBoardsAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return ([], CardProblem.NoKey);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/boards");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return ([], CardProblem.NotAllowed);
            if (!response.IsSuccessStatusCode) return ([], CardProblem.Unreachable);
            return (await response.Content.ReadFromJsonAsync(BoardJsonContext.Default.ListBoardChoice, cancellationToken) ?? [], CardProblem.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ([], CardProblem.Unreachable);
        }
    }

    public async Task<(CardResult? Card, CardProblem Problem)> AddCardAsync(string? apiKey, Guid? boardId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (null, CardProblem.NoKey);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/boards/cards")
            {
                Content = JsonContent.Create(new CardRequest(boardId, text), BoardJsonContext.Default.CardRequest)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, CardProblem.NotAllowed);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return (null, CardProblem.Refused);
            if (!response.IsSuccessStatusCode) return (null, CardProblem.Unreachable);
            var card = await response.Content.ReadFromJsonAsync(BoardJsonContext.Default.CardResult, cancellationToken);
            return card is null ? (null, CardProblem.Unreachable) : (card, CardProblem.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, CardProblem.Unreachable);
        }
    }
}

// Cards waiting for the server, in Application Support - only the text typed and the board chosen.
internal sealed class CardOutbox
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GWS Sentinel", "card-outbox.json");

    public IReadOnlyList<QueuedCard> List()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize(File.ReadAllText(_path), BoardJsonContext.Default.ListQueuedCard) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Add(QueuedCard card) => Write([.. List(), card]);

    public void Replace(IReadOnlyList<QueuedCard> cards) => Write([.. cards]);

    private void Write(List<QueuedCard> cards)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(cards, BoardJsonContext.Default.ListQueuedCard));
    }
}
