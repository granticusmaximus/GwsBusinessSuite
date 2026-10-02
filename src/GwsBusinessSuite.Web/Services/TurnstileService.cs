using System.Net.Http.Json;
using System.Text.Json;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.Services;

public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";
    public string SiteKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string[] AllowedHostnames { get; set; } = [];
}

public enum TurnstileResult { Verified, Rejected, Unavailable }

public static class ContactFormProtection
{
    // Check the resolved page, so changing the posted path cannot disable verification.
    public static bool AppliesTo(CmsSite site, CmsPage page, IConfiguration configuration) =>
        string.Equals(site.Slug, configuration["Canvas:SiteSlug"], StringComparison.OrdinalIgnoreCase)
        && page.ParentPageId is null
        && string.Equals(page.Slug, "contact", StringComparison.OrdinalIgnoreCase);

    // Empty means protected but not configured; null means this is an unrelated form.
    public static string? SiteKeyFor(CmsSite site, CmsPage page, IConfiguration configuration) =>
        AppliesTo(site, page, configuration) ? configuration["Turnstile:SiteKey"] ?? string.Empty : null;
}

public sealed class TurnstileService(
    HttpClient client, IOptions<TurnstileOptions> options, ILogger<TurnstileService> logger)
{
    public const string Action = "contact";
    // The email-signup widget's action - a token minted for one form can't be replayed on the other.
    public const string SubscribeAction = "subscribe";
    public const string ResponseField = "cf-turnstile-response";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.SiteKey) && !string.IsNullOrWhiteSpace(options.Value.SecretKey)
        && options.Value.AllowedHostnames.Length > 0;

    public string? SiteKey => IsConfigured ? options.Value.SiteKey : null;

    public async Task<TurnstileResult> VerifyAsync(
        string token, string hostname, string? remoteIp, CancellationToken cancellationToken = default, string expectedAction = Action)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SiteKey) || string.IsNullOrWhiteSpace(settings.SecretKey)
            || settings.AllowedHostnames.Length == 0)
        {
            logger.LogWarning("Contact form verification is not configured.");
            return TurnstileResult.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048
            || !settings.AllowedHostnames.Contains(hostname, StringComparer.OrdinalIgnoreCase))
            return TurnstileResult.Rejected;

        var fields = new Dictionary<string, string>
        {
            ["secret"] = settings.SecretKey,
            ["response"] = token
        };
        if (!string.IsNullOrWhiteSpace(remoteIp)) fields["remoteip"] = remoteIp;

        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await client.PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify", content, cancellationToken);
            if (!response.IsSuccessStatusCode) return TurnstileResult.Unavailable;
            var result = await response.Content.ReadFromJsonAsync<VerificationResponse>(cancellationToken);
            return result is { Success: true }
                && string.Equals(result.Hostname, hostname, StringComparison.OrdinalIgnoreCase)
                && result.Action == expectedAction
                ? TurnstileResult.Verified : TurnstileResult.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Do not log tokens, secrets, or message contents.
            logger.LogWarning("Contact form verification service is unavailable ({ErrorType}).", ex.GetType().Name);
            return TurnstileResult.Unavailable;
        }
    }

    private sealed record VerificationResponse(bool Success, string? Hostname, string? Action);
}
