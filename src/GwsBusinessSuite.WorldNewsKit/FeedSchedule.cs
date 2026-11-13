using System.Net;

namespace GwsBusinessSuite.WorldNewsKit;

// One feed's health (World News trust plan 1.5, 13-11-26): what it last answered, its validators
// for conditional requests, and when it may next be read.
public sealed record FeedHealth(
    string Url,
    string? ETag = null,
    string? LastModified = null,
    int? LastStatus = null,
    DateTimeOffset? LastAttempt = null,
    DateTimeOffset? LastSuccess = null,
    string? LastError = null,
    int Failures = 0,
    int EmptyReads = 0,
    long Added = 0,
    DateTimeOffset? SkipUntil = null);

// Polite, adaptive polling. A refusal (401/403/405/451) pauses the feed for a day, as the server
// does for any site that refuses it; repeated failures back off to an hour; a feed that keeps
// returning nothing new is read less often, up to every 30 minutes, and comes back to every read
// the moment it has something.
public static class FeedSchedule
{
    public static readonly TimeSpan RefusalPause = TimeSpan.FromDays(1);
    public static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxQuietBackoff = TimeSpan.FromMinutes(30);
    public const int QuietReadsBeforeBackoff = 3;

    public static bool ShouldRead(FeedHealth? health, DateTimeOffset now) =>
        health?.SkipUntil is not { } until || now >= until;

    public static bool IsRefusal(int status) => status is 401 or 403 or 405 or 451;

    public static FeedHealth AfterFailure(FeedHealth health, int? status, string error, DateTimeOffset now)
    {
        var failures = health.Failures + 1;
        var pause = status is { } code && IsRefusal(code)
            ? RefusalPause
            : failures < 2 ? TimeSpan.Zero : Min(TimeSpan.FromMinutes(5 * Math.Pow(2, failures - 2)), MaxFailureBackoff);
        return health with
        {
            LastStatus = status, LastAttempt = now, LastError = error, Failures = failures,
            SkipUntil = pause > TimeSpan.Zero ? now + pause : null,
        };
    }

    // A good answer (200 or 304) with how many reports were new.
    public static FeedHealth AfterSuccess(FeedHealth health, int status, int added, string? etag, string? lastModified, DateTimeOffset now)
    {
        var empty = added > 0 ? 0 : health.EmptyReads + 1;
        var quiet = empty < QuietReadsBeforeBackoff ? TimeSpan.Zero : Min(TimeSpan.FromMinutes(5 * (empty / QuietReadsBeforeBackoff)), MaxQuietBackoff);
        return health with
        {
            LastStatus = status, LastAttempt = now, LastSuccess = now, LastError = null, Failures = 0, EmptyReads = empty,
            Added = health.Added + added, ETag = etag ?? health.ETag, LastModified = lastModified ?? health.LastModified,
            SkipUntil = quiet > TimeSpan.Zero ? now + quiet : null,
        };
    }

    // Conditional-request headers from what the feed sent last time.
    public static void AddValidators(HttpRequestMessage request, FeedHealth? health)
    {
        if (health?.ETag is { Length: > 0 } etag && EntityTagHeaderValueTryParse(etag) is { } tag) request.Headers.IfNoneMatch.Add(tag);
        if (health?.LastModified is { Length: > 0 } modified && DateTimeOffset.TryParse(modified, out var since)) request.Headers.IfModifiedSince = since;
    }

    public static bool IsNotModified(HttpStatusCode status) => status == HttpStatusCode.NotModified;

    private static System.Net.Http.Headers.EntityTagHeaderValue? EntityTagHeaderValueTryParse(string value) =>
        System.Net.Http.Headers.EntityTagHeaderValue.TryParse(value, out var tag) ? tag : null;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
