using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Infrastructure.Services;

// Thin, raw-JSON HTTP client over Notion's public API - no OAuth (an internal-integration
// Bearer token, matching every other integration in this app), a pinned Notion-Version so
// the response shape doesn't shift under us as Notion evolves the API. Raw JsonDocument
// parsing rather than source-generated contracts, matching CjAffiliateService's preference -
// Notion's API is clean, well-documented JSON, so none of CJ's defensive multi-scheme
// probing is needed here.
public sealed class NotionService(HttpClient httpClient) : INotionService
{
    public const string NotionVersion = "2026-03-11";
    private const int MaxImportedFileBytes = 25 * 1024 * 1024;

    public async Task<NotionValidationResult> ValidateConnectionAsync(string integrationToken, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "users/me", integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new NotionValidationResult(false, ExtractErrorMessage(body, response.StatusCode), null);
        }

        using var document = JsonDocument.Parse(body);
        var workspaceName = document.RootElement.TryGetProperty("bot", out var bot)
            && bot.TryGetProperty("workspace_name", out var workspaceNameElement)
            && workspaceNameElement.ValueKind == JsonValueKind.String
                ? workspaceNameElement.GetString()
                : null;

        return new NotionValidationResult(true, "Connected.", workspaceName);
    }

    public async Task<NotionPage> SearchAsync(string integrationToken, string? cursor, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?> { ["page_size"] = 100 };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            payload["start_cursor"] = cursor;
        }

        return await PostPageAsync(integrationToken, "search", payload, cancellationToken);
    }

    public async Task<NotionPage> GetBlockChildrenAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default)
    {
        var path = $"blocks/{Uri.EscapeDataString(blockId)}/children?page_size=100"
            + (string.IsNullOrWhiteSpace(cursor) ? string.Empty : $"&start_cursor={Uri.EscapeDataString(cursor)}");
        using var request = CreateRequest(HttpMethod.Get, path, integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, $"retrieving block children for {blockId}");
        return ParsePage(body);
    }

    public async Task<JsonElement?> GetPageAsync(string integrationToken, string pageId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}", integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    public async Task<NotionMarkdownPage?> GetPageMarkdownAsync(
        string integrationToken,
        string pageId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"pages/{Uri.EscapeDataString(pageId)}/markdown?include_transcript=true",
            integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var markdown = root.TryGetProperty("markdown", out var markdownElement)
            && markdownElement.ValueKind == JsonValueKind.String
                ? markdownElement.GetString() ?? string.Empty
                : string.Empty;
        var truncated = root.TryGetProperty("truncated", out var truncatedElement)
            && truncatedElement.ValueKind == JsonValueKind.True;
        var unknownBlockIds = root.TryGetProperty("unknown_block_ids", out var unknownElement)
            && unknownElement.ValueKind == JsonValueKind.Array
                ? unknownElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .ToList()
                : [];

        return new NotionMarkdownPage(markdown, truncated, unknownBlockIds);
    }

    public async Task<JsonElement?> GetDatabaseAsync(string integrationToken, string databaseId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"data_sources/{Uri.EscapeDataString(databaseId)}", integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    public async Task<NotionPage> QueryDatabaseAsync(string integrationToken, string databaseId, string? cursor, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?> { ["page_size"] = 100 };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            payload["start_cursor"] = cursor;
        }

        return await PostPageAsync(integrationToken, $"data_sources/{Uri.EscapeDataString(databaseId)}/query", payload, cancellationToken);
    }

    public async Task<JsonElement?> GetViewAsync(string integrationToken, string viewId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"views/{Uri.EscapeDataString(viewId)}", integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    public async Task<NotionPage> ListCommentsAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default)
    {
        var path = $"comments?block_id={Uri.EscapeDataString(blockId)}&page_size=100"
            + (string.IsNullOrWhiteSpace(cursor) ? string.Empty : $"&start_cursor={Uri.EscapeDataString(cursor)}");
        using var request = CreateRequest(HttpMethod.Get, path, integrationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, $"retrieving comments for {blockId}");
        return ParsePage(body);
    }

    public async Task<NotionFileDownload> DownloadFileAsync(
        string fileUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Notion returned an invalid file URL.");
        }

        // This request deliberately carries no Notion bearer token. The API returns a
        // short-lived signed URL whose query string is the authorization; forwarding the
        // integration secret to an object-storage host would disclose it.
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxImportedFileBytes)
        {
            throw new InvalidOperationException("Notion file exceeds Sentinel's 25 MB import limit.");
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (content.Length > MaxImportedFileBytes)
        {
            throw new InvalidOperationException("Notion file exceeds Sentinel's 25 MB import limit.");
        }

        var headerName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        var fileName = SafeFileName(headerName)
            ?? SafeFileName(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? string.Empty))
            ?? "notion-file";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new NotionFileDownload(fileName, contentType, content);
    }

    public async Task UpdatePageAsync(string integrationToken, string pageId, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"pages/{Uri.EscapeDataString(pageId)}", integrationToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, $"updating page {pageId}");
    }

    public async Task ReplaceBlockChildrenAsync(string integrationToken, string blockId, IReadOnlyList<object> children, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"blocks/{Uri.EscapeDataString(blockId)}/children", integrationToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { erase_content = true, children }), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, $"replacing block children for {blockId}");
    }

    private async Task<NotionPage> PostPageAsync(string integrationToken, string path, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path, integrationToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, $"calling {path}");
        return ParsePage(body);
    }

    private static NotionPage ParsePage(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var results = root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
            ? resultsElement.EnumerateArray().Select(item => item.Clone()).ToList()
            : [];
        var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) && hasMoreElement.ValueKind == JsonValueKind.True;
        var nextCursor = root.TryGetProperty("next_cursor", out var cursorElement) && cursorElement.ValueKind == JsonValueKind.String
            ? cursorElement.GetString()
            : null;

        return new NotionPage(results, hasMore, nextCursor);
    }

    private static string ExtractErrorMessage(string body, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString() ?? statusCode.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall through to the status-code-only message below.
        }

        return $"Notion API request failed with status {(int)statusCode}.";
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string body,
        string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException(
            $"Notion API failed while {operation}: {ExtractErrorMessage(body, response.StatusCode)}",
            null,
            response.StatusCode);
    }

    private static string? SafeFileName(string? value)
    {
        var candidate = Path.GetFileName(value?.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string integrationToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integrationToken);
        request.Headers.Add("Notion-Version", NotionVersion);
        return request;
    }
}
