using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.WebCrawl;

// A site's robots.txt, read the way RFC 9309 says: the group for this crawler's product token if
// the site has one, otherwise the "*" group; the longest matching rule wins and Allow wins a tie;
// "*" matches anything and "$" anchors the end. Crawl-delay and Sitemap lines are kept too.
public sealed class RobotsTxt
{
    public static readonly RobotsTxt AllowAll = new([], null, []);
    private const int MaxRules = 1000;

    private readonly IReadOnlyList<Rule> _rules;

    private RobotsTxt(IReadOnlyList<Rule> rules, double? crawlDelaySeconds, IReadOnlyList<string> sitemaps)
    {
        _rules = rules;
        CrawlDelaySeconds = crawlDelaySeconds;
        Sitemaps = sitemaps;
    }

    public double? CrawlDelaySeconds { get; }
    public IReadOnlyList<string> Sitemaps { get; }

    public static RobotsTxt Parse(string text, string productToken)
    {
        var groups = new List<(List<string> Agents, List<(string Field, string Value)> Lines)>();
        var sitemaps = new List<string>();
        (List<string> Agents, List<(string Field, string Value)> Lines)? current = null;
        var lastWasAgent = false;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (field)
            {
                case "user-agent":
                    if (current is null || !lastWasAgent)
                    {
                        current = ([], []);
                        groups.Add(current.Value);
                    }
                    current.Value.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "sitemap":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var sitemap) && sitemap.Scheme is "http" or "https") sitemaps.Add(sitemap.AbsoluteUri);
                    break;
                case "allow" or "disallow" or "crawl-delay":
                    current?.Lines.Add((field, value));
                    lastWasAgent = false;
                    break;
            }
        }

        var token = productToken.ToLowerInvariant();
        var chosen = groups.Where(g => g.Agents.Contains(token)).ToList();
        if (chosen.Count == 0) chosen = groups.Where(g => g.Agents.Contains("*")).ToList();

        var rules = new List<Rule>();
        double? delay = null;
        foreach (var (field, value) in chosen.SelectMany(g => g.Lines))
        {
            if (field == "crawl-delay")
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    delay = Math.Max(delay ?? 0, seconds);
                continue;
            }
            // An empty Disallow allows everything; an empty Allow says nothing.
            if (value.Length == 0 || rules.Count >= MaxRules) continue;
            rules.Add(new Rule(value, field == "allow"));
        }
        return new RobotsTxt(rules, delay, sitemaps.Distinct().Take(50).ToList());
    }

    public bool IsAllowed(string pathAndQuery)
    {
        var path = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery;
        if (path == "/robots.txt") return true;
        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(path)) continue;
            if (best is null || rule.Pattern.Length > best.Pattern.Length || (rule.Pattern.Length == best.Pattern.Length && rule.Allow))
                best = rule;
        }
        return best?.Allow ?? true;
    }

    private sealed class Rule(string pattern, bool allow)
    {
        private readonly Regex _regex = new(
            "^" + Regex.Escape(pattern.EndsWith('$') ? pattern[..^1] : pattern).Replace("\\*", ".*") + (pattern.EndsWith('$') ? "$" : string.Empty),
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

        public string Pattern { get; } = pattern;
        public bool Allow { get; } = allow;

        public bool Matches(string path)
        {
            try { return _regex.IsMatch(path); }
            catch (RegexMatchTimeoutException) { return !Allow; }
        }
    }
}
