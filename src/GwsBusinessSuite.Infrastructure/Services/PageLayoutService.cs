using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class PageLayoutService(
    IHostEnvironment hostEnvironment,
    IOptions<CmsBuilderOptions> options) : IPageLayoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // JSX content extraction (same patterns as ReactPageBuilderService)
    private static readonly Regex JsxHeadingRegex = new(@"<h([1-4])\b[^>]*>([\s\S]*?)</h\1>", RegexOptions.Compiled);
    private static readonly Regex JsxParaRegex = new(@"<p\b[^>]*>([\s\S]*?)</p>", RegexOptions.Compiled);
    private static readonly Regex JsxButtonRegex = new(@"<(?:button|Link|a)\b[^>]*>([^<{]{1,120})</(?:button|Link|a)>", RegexOptions.Compiled);
    private static readonly Regex JsxImgRegex = new(@"<img\b[^>]*(?:src=(?:""(?<src>[^""]+)""|\{[^}]+\}))[^>]*/?>", RegexOptions.Compiled);
    private static readonly Regex StripTagsRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex RouteRegex = new(@"<Route\s+path=""(?<path>[^""]+)""\s+element={<(?<component>[A-Za-z0-9_]+)", RegexOptions.Compiled);

    public async Task<PageLayout> LoadLayoutAsync(string pageKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
            return CreateDefaultLayout(pageKey);

        var layoutPath = GetLayoutFilePath(pageKey);
        if (File.Exists(layoutPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(layoutPath, cancellationToken);
                var layout = JsonSerializer.Deserialize<PageLayout>(json, JsonOptions);
                if (layout is not null)
                {
                    EnsureColumnsPopulated(layout);
                    return layout;
                }
            }
            catch
            {
                // Fall through to JSX parse
            }
        }

        // Try to build an initial layout from the existing JSX source
        var jsxPath = Path.Combine(GetReactPagesDirectory(), $"{pageKey}.jsx");
        if (File.Exists(jsxPath))
        {
            try
            {
                var source = await File.ReadAllTextAsync(jsxPath, cancellationToken);
                var routePath = await ResolveRoutePathAsync(pageKey, cancellationToken);
                return ParseLayoutFromJsx(pageKey, source, routePath);
            }
            catch
            {
                // Fall through to default
            }
        }

        return CreateDefaultLayout(pageKey);
    }

    public async Task SaveLayoutAsync(PageLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var dir = GetLayoutDataDirectory();
        Directory.CreateDirectory(dir);
        var path = GetLayoutFilePath(layout.PageKey);
        var json = JsonSerializer.Serialize(layout, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    // ── Path helpers ──────────────────────────────────────────────────────

    private string GetReactAppRoot()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(hostEnvironment.ContentRootPath, "..", ".."));
        var configured = NormalizePath(options.Value.ReactAppRelativePath);
        var configured_abs = Path.GetFullPath(Path.Combine(repoRoot, configured));
        if (Directory.Exists(configured_abs))
            return configured_abs;

        foreach (var fallback in new[] { "apps/public-site", "app/public-site", "public-site" })
        {
            var abs = Path.GetFullPath(Path.Combine(repoRoot, fallback));
            if (Directory.Exists(abs))
                return abs;
        }

        return configured_abs;
    }

    private string GetReactPagesDirectory() =>
        Path.Combine(GetReactAppRoot(), "src", "pages");

    private string GetLayoutDataDirectory() =>
        Path.Combine(GetReactAppRoot(), "src", "pages", "data");

    private string GetLayoutFilePath(string pageKey) =>
        Path.Combine(GetLayoutDataDirectory(), $"{pageKey}.layout.json");

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "apps/public-site" : path.Trim().Replace('\\', '/').Trim('/');

    // ── Route resolution ──────────────────────────────────────────────────

    private async Task<string> ResolveRoutePathAsync(string pageKey, CancellationToken cancellationToken)
    {
        var appFilePath = Path.Combine(GetReactAppRoot(), "src", "App.jsx");
        if (!File.Exists(appFilePath))
            return InferRoutePath(pageKey);

        var appSource = await File.ReadAllTextAsync(appFilePath, cancellationToken);
        foreach (Match m in RouteRegex.Matches(appSource))
        {
            if (string.Equals(m.Groups["component"].Value, pageKey, StringComparison.OrdinalIgnoreCase))
                return m.Groups["path"].Value;
        }

        return InferRoutePath(pageKey);
    }

    private static string InferRoutePath(string pageKey) =>
        string.Equals(pageKey, "Home", StringComparison.OrdinalIgnoreCase) ? "/" : "/" + pageKey.ToLowerInvariant();

    // ── JSX parse → PageLayout ────────────────────────────────────────────

    private PageLayout ParseLayoutFromJsx(string pageKey, string source, string routePath)
    {
        var returnIdx = source.IndexOf("return (", StringComparison.Ordinal);
        if (returnIdx < 0) returnIdx = source.IndexOf("return(", StringComparison.Ordinal);
        var jsxSource = returnIdx >= 0 ? source[returnIdx..] : source;

        var widgets = new List<LayoutWidget>();

        foreach (Match m in JsxHeadingRegex.Matches(jsxSource))
        {
            var text = CleanText(m.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(text) || text.Contains('{')) continue;
            var level = m.Groups[1].Value;
            widgets.Add(new LayoutWidget
            {
                WidgetType = "heading",
                Props = new() { ["level"] = $"h{level}", ["text"] = text, ["align"] = "left" }
            });
        }

        foreach (Match m in JsxParaRegex.Matches(jsxSource))
        {
            var text = CleanText(m.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(text) || text.Contains('{') || text.Length < 8) continue;
            widgets.Add(new LayoutWidget
            {
                WidgetType = "paragraph",
                Props = new() { ["text"] = text, ["align"] = "left" }
            });
        }

        foreach (Match m in JsxButtonRegex.Matches(jsxSource))
        {
            var label = CleanText(m.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(label) || label.Contains('{')) continue;
            widgets.Add(new LayoutWidget
            {
                WidgetType = "button",
                Props = new() { ["label"] = label, ["href"] = "#", ["variant"] = "primary", ["size"] = "md" }
            });
        }

        foreach (Match m in JsxImgRegex.Matches(jsxSource))
        {
            var src = m.Groups["src"].Value.Trim();
            widgets.Add(new LayoutWidget
            {
                WidgetType = "image",
                Props = new() { ["src"] = src, ["alt"] = "", ["width"] = "full" }
            });
        }

        if (widgets.Count == 0)
        {
            widgets.Add(new LayoutWidget
            {
                WidgetType = "heading",
                Props = new() { ["level"] = "h1", ["text"] = SplitPascalCase(pageKey), ["align"] = "left" }
            });
            widgets.Add(new LayoutWidget
            {
                WidgetType = "paragraph",
                Props = new() { ["text"] = "Add content to this page using the widget library.", ["align"] = "left" }
            });
        }

        return new PageLayout
        {
            PageKey = pageKey,
            RoutePath = routePath,
            Sections =
            [
                new LayoutSection
                {
                    Label = "Content",
                    Background = "transparent",
                    Padding = "md",
                    ColumnLayout = "full",
                    Columns =
                    [
                        new LayoutColumn { Span = 12, Widgets = widgets }
                    ]
                }
            ]
        };
    }

    // ── Default layout ────────────────────────────────────────────────────

    private static PageLayout CreateDefaultLayout(string pageKey) => new()
    {
        PageKey = pageKey,
        RoutePath = InferRoutePath(pageKey),
        Sections =
        [
            new LayoutSection
            {
                Label = "Main Content",
                Background = "transparent",
                Padding = "md",
                ColumnLayout = "full",
                Columns =
                [
                    new LayoutColumn
                    {
                        Span = 12,
                        Widgets =
                        [
                            new LayoutWidget
                            {
                                WidgetType = "heading",
                                Props = new() { ["level"] = "h1", ["text"] = SplitPascalCase(pageKey), ["align"] = "left" }
                            },
                            new LayoutWidget
                            {
                                WidgetType = "paragraph",
                                Props = new() { ["text"] = "Start building this page by adding widgets from the library.", ["align"] = "left" }
                            }
                        ]
                    }
                ]
            }
        ]
    };

    // ── Guard: ensure every section has the right number of columns ───────

    private static void EnsureColumnsPopulated(PageLayout layout)
    {
        foreach (var section in layout.Sections)
        {
            var expected = section.ColumnLayout switch
            {
                "half-half" => 2,
                "one-third-two-thirds" => 2,
                "two-thirds-one-third" => 2,
                "thirds" => 3,
                _ => 1
            };

            while (section.Columns.Count < expected)
                section.Columns.Add(new LayoutColumn { Span = 12 / expected });
        }
    }

    // ── Text utils ────────────────────────────────────────────────────────

    private static string CleanText(string raw)
    {
        var stripped = StripTagsRegex.Replace(raw, " ");
        return CollapseWhitespaceRegex.Replace(stripped, " ").Trim();
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                sb.Append(' ');
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}
