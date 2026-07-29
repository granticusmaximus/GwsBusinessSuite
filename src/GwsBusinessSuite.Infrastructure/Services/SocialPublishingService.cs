using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SocialPublishingService(
    IAppDbContext db,
    ISecretProtector secretProtector,
    IOllamaService ollama,
    HttpClient http,
    ILogger<SocialPublishingService> logger) : ISocialPublishingService
{
    public async Task<IReadOnlyList<SocialAccountView>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        await db.SocialAccounts.AsNoTracking()
            .OrderBy(item => item.Network)
            .ThenBy(item => item.DisplayName)
            .Select(item => new SocialAccountView(
                item.Id,
                item.Network,
                item.DisplayName,
                item.ExternalAccountId,
                item.IsEnabled,
                item.ProtectedAccessToken != string.Empty,
                item.LastPublishedAt))
            .ToListAsync(cancellationToken);

    public async Task SaveAccountAsync(SocialAccountInput input, CancellationToken cancellationToken = default)
    {
        if (!SocialNetworks.All.Contains(input.Network, StringComparer.Ordinal))
            throw new ArgumentException("Choose Facebook, X, or LinkedIn.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.DisplayName) || string.IsNullOrWhiteSpace(input.ExternalAccountId))
            throw new ArgumentException("Account name and external account id are required.", nameof(input));

        var account = input.Id is { } id
            ? await db.SocialAccounts.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Social account no longer exists.")
            : new SocialAccount
            {
                Network = input.Network,
                DisplayName = input.DisplayName.Trim(),
                ExternalAccountId = input.ExternalAccountId.Trim()
            };

        account.Network = input.Network;
        account.DisplayName = input.DisplayName.Trim();
        account.ExternalAccountId = input.ExternalAccountId.Trim();
        account.IsEnabled = input.IsEnabled;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(input.AccessToken))
            account.ProtectedAccessToken = secretProtector.Protect(input.AccessToken.Trim());
        if (input.Id is null) await db.SocialAccounts.AddAsync(account, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.SocialAccounts.FirstOrDefaultAsync(item => item.Id == accountId, cancellationToken);
        if (account is null) return;
        var used = await db.SocialPostTargets.AnyAsync(item => item.SocialAccountId == accountId, cancellationToken);
        if (used)
        {
            account.IsEnabled = false;
            account.ProtectedAccessToken = string.Empty;
        }
        else
        {
            db.SocialAccounts.Remove(account);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SocialPostView>> GetPostsAsync(CancellationToken cancellationToken = default)
    {
        var posts = await db.SocialPosts.AsNoTracking()
            .Include(item => item.Targets)
            .ThenInclude(item => item.SocialAccount)
            .ToListAsync(cancellationToken);
        return posts.OrderByDescending(item => item.CreatedAt).Take(40).Select(Map).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GenerateVariantsAsync(
        string topic,
        string sourceUrl,
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("Describe what the post should say.", nameof(topic));
        var accounts = await db.SocialAccounts.AsNoTracking()
            .Where(item => accountIds.Contains(item.Id) && item.IsEnabled)
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0) return new Dictionary<Guid, string>();

        var model = (await db.SiteSettings.AsNoTracking()
            .Where(item => item.Id == SiteSettings.WellKnownId)
            .Select(item => item.OllamaModelOverride)
            .FirstOrDefaultAsync(cancellationToken)) ?? "sentinelgpt:latest";
        var networks = string.Join(", ", accounts.Select(item => item.Network).Distinct());
        var prompt = $$"""
            Create one polished social post for each requested network: {{networks}}.
            Subject: {{topic.Trim()}}
            Source link: {{sourceUrl.Trim()}}

            Requirements:
            - Sound like Grant Watson: direct, credible, useful, and human.
            - Do not invent facts, metrics, quotes, or personal experiences.
            - X must be at most 280 characters including the link.
            - Facebook may be conversational; LinkedIn should be professional but not corporate filler.
            - Use at most 3 relevant hashtags. Avoid emojis unless they add meaning.
            - Return strict JSON only, shaped as {"Facebook":"...","X":"...","LinkedIn":"..."}.
            """;
        var raw = await ollama.GenerateAsync(
            model,
            "You are SentinelGPT's social editor. Produce publication-ready copy, not marketing slop.",
            prompt,
            cancellationToken);
        var parsed = ParseVariants(raw);
        return accounts.ToDictionary(
            account => account.Id,
            account => parsed.GetValueOrDefault(account.Network, BuildFallback(topic, sourceUrl, account.Network)));
    }

    public async Task<Guid> SaveDraftAsync(
        string title,
        string sourceUrl,
        IReadOnlyCollection<SocialTargetDraft> targets,
        DateTimeOffset? scheduledFor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A working title is required.", nameof(title));
        if (targets.Count == 0 || targets.Any(target => string.IsNullOrWhiteSpace(target.Content)))
            throw new ArgumentException("Select at least one account and provide its post copy.", nameof(targets));

        var validAccounts = await db.SocialAccounts.AsNoTracking()
            .Where(item => targets.Select(target => target.SocialAccountId).Contains(item.Id) && item.IsEnabled)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (validAccounts.Count != targets.Select(item => item.SocialAccountId).Distinct().Count())
            throw new InvalidOperationException("One or more selected social accounts is unavailable.");

        var post = new SocialPost
        {
            Title = title.Trim(),
            SourceUrl = sourceUrl.Trim(),
            ScheduledFor = scheduledFor,
            Status = scheduledFor.HasValue ? SocialPostStatuses.Scheduled : SocialPostStatuses.Draft,
            Targets = targets.Select(target =>
            {
                var account = validAccounts[target.SocialAccountId];
                var content = target.Content.Trim();
                if (account.Network == SocialNetworks.X && content.Length > 280)
                    throw new ArgumentException("X posts must be 280 characters or fewer.", nameof(targets));
                return new SocialPostTarget
                {
                    SocialAccountId = account.Id,
                    Network = account.Network,
                    Content = content,
                    Status = scheduledFor.HasValue ? SocialPostStatuses.Scheduled : SocialPostStatuses.Draft
                };
            }).ToList()
        };
        await db.SocialPosts.AddAsync(post, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return post.Id;
    }

    public async Task<SocialPublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        var post = await db.SocialPosts.Include(item => item.Targets).ThenInclude(item => item.SocialAccount)
            .FirstOrDefaultAsync(item => item.Id == postId, cancellationToken);
        if (post is null) return new(false, "Post no longer exists.");
        if (post.Targets.Count == 0) return new(false, "Post has no destinations.");

        post.Status = SocialPostStatuses.Publishing;
        await db.SaveChangesAsync(cancellationToken);
        var successes = 0;
        foreach (var target in post.Targets.Where(item => item.Status != SocialPostStatuses.Published))
        {
            try
            {
                var account = target.SocialAccount;
                if (account is null || !account.IsEnabled || string.IsNullOrWhiteSpace(account.ProtectedAccessToken))
                    throw new InvalidOperationException("Account is disconnected or missing a credential.");
                var token = secretProtector.Unprotect(account.ProtectedAccessToken);
                target.ExternalPostId = await PublishTargetAsync(account, target.Content, token, cancellationToken);
                target.Status = SocialPostStatuses.Published;
                target.ErrorMessage = string.Empty;
                account.LastPublishedAt = DateTimeOffset.UtcNow;
                successes++;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Social publishing failed for target {TargetId} on {Network}.", target.Id, target.Network);
                target.Status = SocialPostStatuses.Failed;
                target.ErrorMessage = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500];
            }
        }

        post.PublishedAt = successes > 0 ? DateTimeOffset.UtcNow : null;
        post.Status = successes == post.Targets.Count
            ? SocialPostStatuses.Published
            : successes > 0 ? SocialPostStatuses.PartiallyPublished : SocialPostStatuses.Failed;
        await db.SaveChangesAsync(cancellationToken);
        return new(successes == post.Targets.Count, $"{successes} of {post.Targets.Count} destinations published.");
    }

    public async Task PublishDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var posts = await db.SocialPosts.AsNoTracking()
            .Where(item => item.Status == SocialPostStatuses.Scheduled && item.ScheduledFor != null)
            .ToListAsync(cancellationToken);
        foreach (var post in posts.Where(item => item.ScheduledFor <= now))
            await PublishAsync(post.Id, cancellationToken);
    }

    private async Task<string> PublishTargetAsync(
        SocialAccount account,
        string content,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = account.Network switch
        {
            SocialNetworks.Facebook => BuildFacebookRequest(account, content, token),
            SocialNetworks.X => BuildXRequest(content, token),
            SocialNetworks.LinkedIn => BuildLinkedInRequest(account, content, token),
            _ => throw new InvalidOperationException($"Unsupported social network: {account.Network}")
        };
        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{account.Network} rejected the post ({(int)response.StatusCode}): {ReadApiError(payload)}");
        if (account.Network == SocialNetworks.LinkedIn)
            return response.Headers.TryGetValues("x-restli-id", out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
        using var json = JsonDocument.Parse(payload);
        return account.Network == SocialNetworks.X
            ? json.RootElement.GetProperty("data").GetProperty("id").GetString() ?? string.Empty
            : json.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    private static HttpRequestMessage BuildFacebookRequest(SocialAccount account, string content, string token) =>
        new(HttpMethod.Post, $"https://graph.facebook.com/v24.0/{Uri.EscapeDataString(account.ExternalAccountId)}/feed")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = content,
                ["access_token"] = token
            })
        };

    private static HttpRequestMessage BuildXRequest(string content, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.com/2/tweets")
        {
            Content = JsonContent.Create(new { text = content })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage BuildLinkedInRequest(SocialAccount account, string content, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linkedin.com/rest/posts")
        {
            Content = JsonContent.Create(new
            {
                author = account.ExternalAccountId,
                commentary = content,
                visibility = "PUBLIC",
                distribution = new
                {
                    feedDistribution = "MAIN_FEED",
                    targetEntities = Array.Empty<object>(),
                    thirdPartyDistributionChannels = Array.Empty<object>()
                },
                lifecycleState = "PUBLISHED",
                isReshareDisabledByAuthor = false
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        request.Headers.TryAddWithoutValidation("Linkedin-Version", "202605");
        return request;
    }

    private static IReadOnlyDictionary<string, string> ParseVariants(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw[start..(end + 1)])
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string BuildFallback(string topic, string sourceUrl, string network)
    {
        var text = string.IsNullOrWhiteSpace(sourceUrl) ? topic.Trim() : $"{topic.Trim()}\n\n{sourceUrl.Trim()}";
        return network == SocialNetworks.X && text.Length > 280 ? text[..277] + "..." : text;
    }

    private static string ReadApiError(string payload)
    {
        if (payload.Length > 800) payload = payload[..800];
        return string.IsNullOrWhiteSpace(payload) ? "No error details returned." : payload;
    }

    private static SocialPostView Map(SocialPost post) => new(
        post.Id,
        post.Title,
        post.SourceUrl,
        post.Status,
        post.ScheduledFor,
        post.PublishedAt,
        post.Targets.Select(target => new SocialPostTargetView(
            target.Id,
            target.SocialAccountId,
            target.Network,
            target.SocialAccount?.DisplayName ?? "Disconnected account",
            target.Content,
            target.Status,
            target.ExternalPostId,
            target.ErrorMessage)).ToList());
}
