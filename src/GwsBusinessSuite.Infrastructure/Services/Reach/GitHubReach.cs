using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using Microsoft.Extensions.Options;
using static GwsBusinessSuite.Infrastructure.Services.Reach.ReachText;

namespace GwsBusinessSuite.Infrastructure.Services.Reach;

// GitHub through its REST API: a repository with its README, an issue or pull request with its
// comments, and search. Without a token GitHub allows 60 requests an hour, which is enough for
// reading the odd link; Reach:GitHubToken raises it to 5,000.
public sealed class GitHubReach(IHttpClientFactory http, IOptions<ReachOptions> options) : ReachChannel(http)
{
    private const string Api = "https://api.github.com";
    // github.com paths that aren't owner/repo.
    private static readonly HashSet<string> NotOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "orgs", "settings", "marketplace", "topics", "explore", "features", "sponsors", "notifications", "login", "search", "collections", "trending", "about", "pricing", "enterprise", "apps"
    };

    private string? Token => string.IsNullOrWhiteSpace(options.Value.GitHubToken) ? null : options.Value.GitHubToken.Trim();

    public override string Id => "github";
    public override string Name => "GitHub";
    public override string Reads => "a repository and its README, an issue or pull request with its comments";
    public override string Searches => "repositories, or issues and pull requests (within: issues)";
    public override string? Needs => Token is null ? "Reach:GitHubToken (optional: raises GitHub's limit from 60 to 5,000 requests an hour)" : null;

    public override bool Handles(Uri url)
    {
        if (!url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !url.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = Segments(url);
        return parts.Length >= 2 && !NotOwners.Contains(parts[0]);
    }

    public override async Task<ReachDocument?> ReadAsync(Uri url, CancellationToken ct)
    {
        var parts = Segments(url);
        var (owner, repo) = (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        if (parts.Length >= 4 && parts[2] is "issues" or "pull" && int.TryParse(parts[3], out var number))
            return await IssueAsync(owner, repo, number, ct);
        return await RepositoryAsync(owner, repo, ct);
    }

    private async Task<ReachDocument> RepositoryAsync(string owner, string repo, CancellationToken ct)
    {
        using var doc = await JsonAsync($"{Api}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}", ct, Prepare);
        var r = doc.RootElement;
        string readme;
        try
        {
            readme = await TextAsync($"{Api}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/readme", ct, request =>
            {
                Prepare(request);
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
            });
        }
        catch (ReachException)
        {
            readme = "(No README.)";
        }
        var topics = r.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array ? string.Join(", ", t.EnumerateArray().Select(x => x.GetString())) : "";
        var facts = new Dictionary<string, string>
        {
            ["stars"] = Count(Num(r, "stargazers_count")),
            ["forks"] = Count(Num(r, "forks_count")),
            ["open issues"] = Count(Num(r, "open_issues_count")),
            ["language"] = Str(r, "language") ?? "not stated",
            ["licence"] = Str(Obj(r, "license"), "spdx_id") ?? "none stated",
            ["last pushed"] = Date(Str(r, "pushed_at"))?.ToGwsDate() ?? "?"
        };
        if (topics.Length > 0) facts["topics"] = topics;
        if (Str(r, "homepage") is { Length: > 0 } homepage) facts["homepage"] = homepage;
        if (r.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) facts["archived"] = "yes";
        return new ReachDocument(Name, Str(r, "full_name") ?? $"{owner}/{repo}", Str(r, "html_url") ?? $"https://github.com/{owner}/{repo}",
            (Str(r, "description") is { Length: > 0 } about ? about + "\n\n" : "") + "README:\n" + Limit(readme, TextCharacters),
            Str(Obj(r, "owner"), "login"), Date(Str(r, "created_at")), Facts: facts);
    }

    private async Task<ReachDocument> IssueAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var path = $"{Api}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number}";
        using var doc = await JsonAsync(path, ct, Prepare);
        using var comments = await JsonAsync($"{path}/comments?per_page={MaxComments}", ct, Prepare);
        var i = doc.RootElement;
        var isPull = i.TryGetProperty("pull_request", out _);
        var labels = i.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? string.Join(", ", l.EnumerateArray().Select(x => Str(x, "name")).OfType<string>()) : "";
        var facts = new Dictionary<string, string>
        {
            ["kind"] = isPull ? "pull request" : "issue",
            ["state"] = Str(i, "state") ?? "?",
            ["comments"] = Count(Num(i, "comments"))
        };
        if (labels.Length > 0) facts["labels"] = labels;
        return new ReachDocument(Name, $"{owner}/{repo}#{number}: {Str(i, "title")}", Str(i, "html_url") ?? $"https://github.com/{owner}/{repo}/issues/{number}",
            Limit(Str(i, "body"), TextCharacters), Str(Obj(i, "user"), "login"), Date(Str(i, "created_at")),
            // Bots (deploy previews, CI) say nothing a reader needs.
            comments.RootElement.EnumerateArray().Where(c => Str(Obj(c, "user"), "type") != "Bot").Take(MaxComments)
                .Select(c => new ReachComment(Str(Obj(c, "user"), "login") ?? "someone", Limit(Str(c, "body"), CommentCharacters))).ToList(),
            facts);
    }

    public override async Task<IReadOnlyList<ReachItem>> SearchAsync(string query, string? within, CancellationToken ct)
    {
        var issues = within?.Trim().ToLowerInvariant() is "issues" or "issue" or "prs" or "pull requests";
        using var doc = await JsonAsync($"{Api}/search/{(issues ? "issues" : "repositories")}?q={Uri.EscapeDataString(query)}&per_page=8", ct, Prepare);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return [];
        return items.EnumerateArray().Select(item => issues
            ? new ReachItem(Str(item, "title") ?? "", Str(item, "html_url") ?? "", Str(Obj(item, "user"), "login"), Limit(Str(item, "body"), 200),
                Date(Str(item, "created_at")), $"{Str(item, "state")} · {Count(Num(item, "comments"))} comments")
            : new ReachItem(Str(item, "full_name") ?? "", Str(item, "html_url") ?? "", Str(Obj(item, "owner"), "login"), Str(item, "description"),
                Date(Str(item, "pushed_at")), $"{Count(Num(item, "stargazers_count"))} stars · {Str(item, "language") ?? "no language"}"))
            .ToList();
    }

    public override async Task CheckAsync(CancellationToken ct)
    {
        using var doc = await JsonAsync($"{Api}/rate_limit", ct, Prepare);
        var core = Obj(Obj(doc.RootElement, "resources"), "core");
        if (Num(core, "remaining") is 0) throw new ReachException("GitHub's hourly allowance is used up; it resets within the hour.");
    }

    private void Prepare(HttpRequestMessage request)
    {
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (Token is { } token) request.Headers.Authorization = new("Bearer", token);
    }
}
