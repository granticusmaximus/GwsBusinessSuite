using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace GwsBusinessSuite.Tests;

// Responsive device audit (.claude/plans/responsive-every-device.md). Crawls every page at a
// matrix of real device sizes and records what breaks: sideways overflow, undersized touch
// targets, unreadably small text, controls something else is covering, and console/CSP errors.
//
// Opt-in: it does nothing unless GWS_AUDIT_ADMIN_BASE is set, because it needs a running app
// (local Development instance with PublicSite:AdditionalHosts so the public site is reachable):
//   GWS_AUDIT_ADMIN_BASE=http://localhost:5214  GWS_AUDIT_PUBLIC_BASE=http://127.0.0.1:5214
//   GWS_AUDIT_USER / GWS_AUDIT_PASSWORD  GWS_AUDIT_OUT=<folder>  [GWS_AUDIT_DEVICES=360x780,...]
//   [GWS_AUDIT_ROUTES=/admin,/blog,...]
[Collection("Playwright")]
public sealed class ResponsiveAuditTests(PlaywrightBrowserFixture fixture)
{
    public sealed record Device(string Name, int Width, int Height, bool Touch);

    public static readonly Device[] Devices =
    [
        new("phone-360", 360, 780, true),
        new("phone-390", 390, 844, true),
        new("phone-landscape", 844, 390, true),
        new("tablet-768", 768, 1024, true),
        new("tablet-landscape-1024", 1024, 768, true),
        new("laptop-1280", 1280, 800, false),
        new("laptop-1440", 1440, 900, false),
        new("desktop-1920", 1920, 1080, false),
        new("desktop-2560", 2560, 1440, false),
    ];

    public static readonly string[] PublicRoutes = ["/", "/blog", "/about", "/cv", "/portfolio", "/contact"];

    public static readonly string[] AdminRoutes =
    [
        "/admin", "/admin/affiliate-analytics", "/admin/affiliate-suggestions", "/admin/app-generation",
        "/admin/app-generation-queue", "/admin/appearance/customize", "/admin/appearance/header-footer",
        "/admin/appearance/menus", "/admin/article-editor", "/admin/automation", "/admin/automation/credentials",
        "/admin/automation/help", "/admin/billing", "/admin/business-intelligence", "/admin/cj-ads",
        "/admin/cms-knowledge", "/admin/comments", "/admin/community", "/admin/community/activity",
        "/admin/community/departments", "/admin/community/messages", "/admin/content-studio", "/admin/crm",
        "/admin/deal-scoring", "/admin/dev-tools", "/admin/docker-health", "/admin/email-campaigns",
        "/admin/form-submissions", "/admin/government-intelligence", "/admin/growth", "/admin/live-show",
        "/admin/live-show-recordings", "/admin/localization", "/admin/media", "/admin/mind-maps",
        "/admin/mission-control", "/admin/news-intelligence", "/admin/osint", "/admin/pages", "/admin/pages/all",
        "/admin/podcasts", "/admin/privacy-operations", "/admin/scheduling", "/admin/security-audit",
        "/admin/sentinel", "/admin/seo-audit", "/admin/settings", "/admin/support", "/admin/threat-intel",
        "/admin/users"
    ];

    // Runs inside the page; one call per page/device.
    private const string ChecksScript = """
        (touch) => {
          const vw = window.innerWidth, vh = window.innerHeight, de = document.documentElement;
          const desc = el => {
            let s = el.tagName.toLowerCase();
            if (el.id) s += '#' + el.id;
            const cls = (typeof el.className === 'string' ? el.className : '').trim().split(/\s+/).filter(Boolean).slice(0, 2);
            if (cls.length) s += '.' + cls.join('.');
            const text = (el.innerText || el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 30);
            return text ? s + ' "' + text + '"' : s;
          };
          const visible = el => {
            const cs = getComputedStyle(el);
            if (cs.visibility === 'hidden' || cs.display === 'none' || Number(cs.opacity) === 0) return false;
            const r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0;
          };
          const inScroller = el => {
            for (let p = el.parentElement; p && p !== document.body && p !== de; p = p.parentElement) {
              if (/(auto|scroll|hidden|clip)/.test(getComputedStyle(p).overflowX)) return true;
            }
            return false;
          };
          const result = { overflowX: Math.max(de.scrollWidth, document.body ? document.body.scrollWidth : 0) - vw, overflowers: [], smallTargets: [], smallTargetCount: 0, tinyText: [], tinyTextCount: 0, covered: [], coveredCount: 0 };

          if (result.overflowX > 1) {
            for (const el of document.body.querySelectorAll('*')) {
              const r = el.getBoundingClientRect();
              if (r.width && r.right > vw + 1 && visible(el) && !inScroller(el)) {
                result.overflowers.push(desc(el) + ' (right ' + Math.round(r.right) + 'px)');
                if (result.overflowers.length >= 6) break;
              }
            }
          }

          const interactive = Array.from(document.querySelectorAll('a[href], button, input:not([type=hidden]), select, textarea, [role=button], summary'))
            .filter(el => visible(el) && !el.closest('[aria-hidden=true]') && !el.closest('iframe'));
          for (const el of interactive) {
            const r = el.getBoundingClientRect();
            if (r.bottom < 0 || r.top > vh || r.right < 0 || r.left > vw) continue;
            const min = Math.min(r.width, r.height);
            const isInlineLink = el.tagName === 'A' && getComputedStyle(el).display === 'inline' && el.closest('p, li, td');
            if (touch && min < 32 && !isInlineLink && el.type !== 'checkbox' && el.type !== 'radio') {
              result.smallTargetCount++;
              if (result.smallTargets.length < 6) result.smallTargets.push(desc(el) + ' (' + Math.round(r.width) + 'x' + Math.round(r.height) + ')');
            }
            const cx = Math.min(Math.max(r.left + r.width / 2, 0), vw - 1), cy = Math.min(Math.max(r.top + r.height / 2, 0), vh - 1);
            if (r.left >= 0 && r.right <= vw && r.top >= 0 && r.bottom <= vh && getComputedStyle(el).pointerEvents !== 'none') {
              const hit = document.elementFromPoint(cx, cy);
              if (hit && hit !== el && !el.contains(hit) && !hit.contains(el) && !(el.labels && Array.from(el.labels).some(l => l.contains(hit)))) {
                result.coveredCount++;
                if (result.covered.length < 6) result.covered.push(desc(el) + ' under ' + desc(hit));
              }
            }
          }

          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          const seen = new Set();
          while (walker.nextNode()) {
            const node = walker.currentNode, el = node.parentElement;
            if (!el || seen.has(el) || !node.textContent.trim()) continue;
            seen.add(el);
            if (!visible(el)) continue;
            const size = parseFloat(getComputedStyle(el).fontSize);
            if (size < 12) {
              result.tinyTextCount++;
              if (result.tinyText.length < 6) result.tinyText.push(desc(el) + ' (' + size + 'px)');
            }
          }
          return JSON.stringify(result);
        }
        """;

    [Fact]
    public async Task AuditEveryPageOnEveryDevice()
    {
        var adminBase = Environment.GetEnvironmentVariable("GWS_AUDIT_ADMIN_BASE");
        if (string.IsNullOrWhiteSpace(adminBase)) return; // opt-in
        var publicBase = Environment.GetEnvironmentVariable("GWS_AUDIT_PUBLIC_BASE") ?? adminBase;
        var outDir = Environment.GetEnvironmentVariable("GWS_AUDIT_OUT") ?? Path.Combine(Path.GetTempPath(), "gws-responsive-audit");
        Directory.CreateDirectory(outDir);
        var devices = Filter(Devices, Environment.GetEnvironmentVariable("GWS_AUDIT_DEVICES"), d => $"{d.Width}x{d.Height}", d => d.Name);
        var routeFilter = Environment.GetEnvironmentVariable("GWS_AUDIT_ROUTES");
        var adminRoutes = Filter(AdminRoutes, routeFilter, r => r, r => r);
        var publicRoutes = Filter(PublicRoutes, routeFilter, r => r, r => r);

        // Sign in once and reuse the cookie for every device.
        var loginContext = await fixture.Browser.NewContextAsync();
        var login = await loginContext.NewPageAsync();
        await login.GotoAsync($"{adminBase}/admin/login");
        await login.FillAsync("#gws-login-username", Environment.GetEnvironmentVariable("GWS_AUDIT_USER") ?? "");
        await login.FillAsync("#gws-login-password", Environment.GetEnvironmentVariable("GWS_AUDIT_PASSWORD") ?? "");
        await login.ClickAsync(".gws-login-submit");
        await login.WaitForTimeoutAsync(4000);
        var storage = await loginContext.StorageStateAsync();
        await loginContext.DisposeAsync();

        var rows = new List<Dictionary<string, object?>>();
        foreach (var device in devices)
        {
            var context = await fixture.Browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = device.Width, Height = device.Height },
                IsMobile = device.Touch && device.Width < 1000,
                HasTouch = device.Touch,
                StorageState = storage
            });
            var targets = publicRoutes.Select(r => (Url: publicBase + r, Route: r, Area: "public"))
                .Concat(adminRoutes.Select(r => (Url: adminBase + r, Route: r, Area: "admin")));
            foreach (var (url, route, area) in targets)
            {
                var page = await context.NewPageAsync();
                var errors = new List<string>();
                page.Console += (_, m) => { if (m.Type == "error" && errors.Count < 5) errors.Add(m.Text.Length > 140 ? m.Text[..140] : m.Text); };
                page.PageError += (_, e) => { if (errors.Count < 5) errors.Add("pageerror: " + (e.Length > 140 ? e[..140] : e)); };
                var row = new Dictionary<string, object?> { ["device"] = device.Name, ["area"] = area, ["route"] = route };
                try
                {
                    var response = await page.GotoAsync(url, new() { Timeout = 30000, WaitUntil = WaitUntilState.Load });
                    row["status"] = response?.Status;
                    await page.WaitForTimeoutAsync(area == "admin" ? 2500 : 1200);
                    var json = await page.EvaluateAsync<string>(ChecksScript, device.Touch);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var property in doc.RootElement.EnumerateObject())
                        row[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                            ? property.Value.EnumerateArray().Select(v => v.GetString()).ToList()
                            : property.Value.GetInt32();
                    var shot = Path.Combine(outDir, "screens", device.Name, Slug(area, route) + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(shot)!);
                    await page.ScreenshotAsync(new() { Path = shot });
                    row["screenshot"] = Path.GetRelativePath(outDir, shot);
                }
                catch (Exception ex)
                {
                    row["failure"] = ex.Message.Split('\n')[0];
                }
                row["errors"] = errors;
                rows.Add(row);
                await page.CloseAsync();
            }
            await context.DisposeAsync();
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "audit.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(outDir, "audit.md"), Summarize(rows));
    }

    private static int Num(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var v) && v is int i ? i : 0;

    public static int Score(Dictionary<string, object?> row) =>
        (row.ContainsKey("failure") ? 50 : 0)
        + (Num(row, "overflowX") > 1 ? 20 : 0)
        + Num(row, "coveredCount") * 3
        + Math.Min(Num(row, "smallTargetCount"), 20)
        + Math.Min(Num(row, "tinyTextCount"), 10) / 2;

    private static string Summarize(List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder("# Responsive device audit\n\n");
        sb.AppendLine($"{rows.Count} page loads. Score: overflow 20, failed load 50, covered control 3 each, small touch target 1 each (max 20), tiny text 0.5 each (max 5).\n");
        sb.AppendLine("## Worst pages (summed across devices)\n");
        sb.AppendLine("| Route | Score | Sideways overflow on | Covered | Small targets | Tiny text |");
        sb.AppendLine("| --- | ---: | --- | ---: | ---: | ---: |");
        foreach (var g in rows.GroupBy(r => $"{r["area"]} {r["route"]}").Select(g => (Key: g.Key, Rows: g.ToList(), Score: g.Sum(Score))).OrderByDescending(g => g.Score).Take(60))
        {
            var overflow = string.Join(", ", g.Rows.Where(r => Num(r, "overflowX") > 1).Select(r => $"{r["device"]} (+{Num(r, "overflowX")}px)"));
            sb.AppendLine($"| {g.Key} | {g.Score} | {overflow} | {g.Rows.Sum(r => Num(r, "coveredCount"))} | {g.Rows.Sum(r => Num(r, "smallTargetCount"))} | {g.Rows.Sum(r => Num(r, "tinyTextCount"))} |");
        }
        sb.AppendLine("\n## By device\n\n| Device | Pages with sideways overflow | Covered controls | Small targets | Tiny text | Failed loads |\n| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var g in rows.GroupBy(r => (string)r["device"]!))
            sb.AppendLine($"| {g.Key} | {g.Count(r => Num(r, "overflowX") > 1)} | {g.Sum(r => Num(r, "coveredCount"))} | {g.Sum(r => Num(r, "smallTargetCount"))} | {g.Sum(r => Num(r, "tinyTextCount"))} | {g.Count(r => r.ContainsKey("failure"))} |");
        return sb.ToString();
    }

    private static string Slug(string area, string route) =>
        area + (route == "/" ? "-home" : route.Replace('/', '-'));

    private static T[] Filter<T>(T[] items, string? filter, Func<T, string> a, Func<T, string> b)
    {
        if (string.IsNullOrWhiteSpace(filter)) return items;
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Where(i => wanted.Contains(a(i)) || wanted.Contains(b(i))).ToArray();
    }
}
