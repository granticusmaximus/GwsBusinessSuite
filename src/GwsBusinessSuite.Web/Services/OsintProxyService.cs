using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Web.Services;

public sealed class OsintProxyService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<OsintProxyService> logger)
{
    public const string ShellPrefix = "/admin/osint-root";

    private const int GatewayTimeoutStatusCode = StatusCodes.Status504GatewayTimeout;

    // Entity-resolution lookups (aircraft/vessel/company/... -> Wikidata/OpenSanctions inside the
    // osiris-intel sidecar) are the slow part of OSINT Watch, and they're all served through this
    // Next.js API route. The upstream response never varies by which gwssuite user is asking -
    // ForwardedRequestHeaders below strips auth/cookies/client IP before forwarding - so a shared
    // cache here helps every viewer, not just whoever happened to trigger the first lookup.
    private static readonly TimeSpan ApiResponseCacheDuration = TimeSpan.FromMinutes(10);
    private const long MaxCacheableApiResponseBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> ForwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Language",
        "Content-Type",
        "If-Modified-Since",
        "If-None-Match",
        "Next-Router-Prefetch",
        "Next-Router-State-Tree",
        "Next-Url",
        "Purpose",
        "Range",
        "RSC"
    };

    private static readonly HashSet<string> SuppressedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Content-Security-Policy",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Set-Cookie",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "X-Frame-Options"
    };

    private static readonly Regex DocumentRootAttributeRegex = new(
        "(?<attribute>\\b(?:href|src|action)=[\\\"'])/(?!(?:_next|api|data)(?:/|[\\\"']))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task ForwardAsync(
        HttpContext context,
        string targetPath,
        bool rewriteDocumentPaths,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRelativeTarget(targetPath, context.Request.QueryString, out var relativeTarget))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid OSINT Watch path.", cancellationToken);
            return;
        }

        var isCacheableApiGet = HttpMethods.IsGet(context.Request.Method)
            && targetPath.StartsWith("api/", StringComparison.Ordinal);
        var cacheKey = isCacheableApiGet ? $"osint-proxy:api-get:{relativeTarget}" : null;

        if (cacheKey is not null
            && cache.TryGetValue(cacheKey, out CachedApiResponse? cached)
            && cached is not null)
        {
            context.Response.StatusCode = cached.StatusCode;
            if (cached.ContentType is not null)
            {
                context.Response.ContentType = cached.ContentType;
            }

            await context.Response.Body.WriteAsync(cached.Body, cancellationToken);
            return;
        }

        using var upstreamRequest = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            relativeTarget);

        // Never send the GWS auth cookie, antiforgery token, client IP, or Authorization header
        // into the separately maintained OSIRIS container. The sidecar only needs content and
        // Next.js routing headers. A fixed User-Agent also avoids leaking browser fingerprints.
        upstreamRequest.Headers.TryAddWithoutValidation("User-Agent", "GwsBusinessSuite-OsintProxy/1.0");
        foreach (var header in context.Request.Headers)
        {
            if (!ForwardedRequestHeaders.Contains(header.Key)
                || string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (RequestCanHaveBody(context.Request.Method)
            && (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
        {
            upstreamRequest.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { Length: > 0 } contentType)
            {
                upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        try
        {
            var client = httpClientFactory.CreateClient("osiris");
            using var upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(upstreamResponse.Headers, context.Response.Headers);
            CopyResponseHeaders(upstreamResponse.Content.Headers, context.Response.Headers);

            if (upstreamResponse.Headers.Location is { } location)
            {
                context.Response.Headers.Location = RewriteLocation(location, client.BaseAddress);
            }

            var isHtml = upstreamResponse.Content.Headers.ContentType?.MediaType?.Equals(
                "text/html",
                StringComparison.OrdinalIgnoreCase) == true;
            if (rewriteDocumentPaths && isHtml)
            {
                var html = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
                await context.Response.WriteAsync(RewriteDocumentPaths(html), cancellationToken);
                return;
            }

            if (cacheKey is not null)
            {
                var body = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (upstreamResponse.StatusCode == HttpStatusCode.OK
                    && body.LongLength <= MaxCacheableApiResponseBytes)
                {
                    cache.Set(
                        cacheKey,
                        new CachedApiResponse(
                            (int)upstreamResponse.StatusCode,
                            upstreamResponse.Content.Headers.ContentType?.ToString(),
                            body),
                        ApiResponseCacheDuration);
                }

                await context.Response.Body.WriteAsync(body, cancellationToken);
                return;
            }

            await upstreamResponse.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("OSINT Watch request to {TargetPath} timed out.", targetPath);
            await WriteGatewayFailureAsync(context, GatewayTimeoutStatusCode, "OSINT Watch timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OSINT Watch request to {TargetPath} failed.", targetPath);
            await WriteGatewayFailureAsync(
                context,
                StatusCodes.Status502BadGateway,
                "OSINT Watch is temporarily unavailable.");
        }
    }

    public static string RewriteDocumentPaths(string html)
    {
        var rewritten = DocumentRootAttributeRegex.Replace(
            html,
            match => $"{match.Groups["attribute"].Value}{ShellPrefix}/");

        // Next.js serializes Link props into inline RSC payloads. Rewriting only the rendered
        // anchor is not enough because hydration would otherwise restore the root-relative route.
        return rewritten
            .Replace("\\\"/docs\\\"", $"\\\"{ShellPrefix}/docs\\\"", StringComparison.Ordinal)
            .Replace("\"/docs\"", $"\"{ShellPrefix}/docs\"", StringComparison.Ordinal)
            .Replace("\\\"href\\\":\\\"/\\\"", $"\\\"href\\\":\\\"{ShellPrefix}/\\\"", StringComparison.Ordinal)
            .Replace("\"href\":\"/\"", $"\"href\":\"{ShellPrefix}/\"", StringComparison.Ordinal);
    }

    public static string RewriteLocation(Uri location, Uri? upstreamBaseAddress)
    {
        var locationValue = location.IsAbsoluteUri
            && upstreamBaseAddress is not null
            && string.Equals(location.Host, upstreamBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
            ? location.PathAndQuery + location.Fragment
            : location.OriginalString;

        if (!locationValue.StartsWith("/", StringComparison.Ordinal)
            || locationValue.StartsWith("/_next/", StringComparison.OrdinalIgnoreCase)
            || locationValue.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || locationValue.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
        {
            return locationValue;
        }

        return ShellPrefix + locationValue;
    }

    private static bool TryBuildRelativeTarget(
        string targetPath,
        QueryString queryString,
        out Uri relativeTarget)
    {
        var segments = targetPath.TrimStart('/').Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "." or ".."
                || segment.Contains('\\')
                || segment.Any(char.IsControl)))
        {
            relativeTarget = null!;
            return false;
        }

        var encodedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        relativeTarget = new Uri(encodedPath + queryString.Value, UriKind.Relative);
        return true;
    }

    private static bool RequestCanHaveBody(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method);

    private static void CopyResponseHeaders(
        System.Net.Http.Headers.HttpHeaders source,
        IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (!SuppressedResponseHeaders.Contains(header.Key)
                && !string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
            {
                destination[header.Key] = header.Value.ToArray();
            }
        }
    }

    private static async Task WriteGatewayFailureAsync(
        HttpContext context,
        int statusCode,
        string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }

    private sealed record CachedApiResponse(int StatusCode, string? ContentType, byte[] Body);
}
