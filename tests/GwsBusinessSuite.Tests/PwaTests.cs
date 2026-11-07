using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// The admin app as an installed app (PWA, 07-11-26): the manifest and what it points at, the
// service worker's offline page, and the install offer. The browser test serves wwwroot from a
// real local HTTP server, because a service worker needs a real origin (Playwright's routing
// doesn't reach one).
[Collection("Playwright")]
public sealed partial class PwaTests(PlaywrightBrowserFixture fixture)
{
    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GwsBusinessSuite.Web"));
    private static string WwwRoot => Path.Combine(WebRoot, "wwwroot");

    [Fact]
    public void Manifest_ShouldDescribeAnInstallableAdminApp_WhoseIconsAndShortcutsExist()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(WwwRoot, "manifest.webmanifest")));
        var root = manifest.RootElement;

        root.GetProperty("name").GetString().Should().Be("GWS Business Suite");
        root.GetProperty("short_name").GetString()!.Length.Should().BeLessThanOrEqualTo(12, "home screens cut longer names");
        root.GetProperty("start_url").GetString().Should().Be("/admin");
        root.GetProperty("scope").GetString().Should().Be("/admin");
        root.GetProperty("display").GetString().Should().Be("standalone");

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        icons.Select(icon => icon.GetProperty("purpose").GetString()).Should().Contain(["any", "maskable"]);
        foreach (var icon in icons)
        {
            var size = int.Parse(icon.GetProperty("sizes").GetString()!.Split('x')[0]);
            PngSize(icon.GetProperty("src").GetString()!).Should().Be((size, size), icon.GetProperty("src").GetString());
        }
        icons.Should().Contain(icon => icon.GetProperty("sizes").GetString() == "512x512", "Android's splash screen and install need 512");
        PngSize("/img/app-icons/apple-touch-icon.png").Should().Be((180, 180));

        // Each shortcut opens a real admin page.
        var routes = Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => PageRoute().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var shortcut in root.GetProperty("shortcuts").EnumerateArray())
        {
            var url = shortcut.GetProperty("url").GetString()!;
            url.Should().StartWith("/admin");
            routes.Should().Contain(url.Split('?')[0]);
        }
    }

    [Fact]
    public void TheAppShell_ShouldLinkTheManifest_AndEveryFileTheOfflinePageNeedsShouldExist()
    {
        var layout = File.ReadAllText(Path.Combine(WebRoot, "Components/Layout/MainLayout.razor"));
        layout.Should().Contain("<link rel=\"manifest\" href=\"manifest.webmanifest\" />")
            .And.Contain("apple-touch-icon").And.Contain("@if (IsAdminRoute)");

        var app = File.ReadAllText(Path.Combine(WebRoot, "Components/App.razor"));
        app.IndexOf("js/pwa.js", StringComparison.Ordinal).Should().BeGreaterThan(app.IndexOf("_framework/blazor.web.js", StringComparison.Ordinal),
            "pwa.js listens for Blazor's enhancedload, so it loads after Blazor");

        var worker = File.ReadAllText(Path.Combine(WwwRoot, "sw.js"));
        var offlineUrl = OfflineUrl().Match(worker).Groups[1].Value;
        var cached = OfflineFiles().Match(worker).Groups[1].Value.Split(',').Select(file => file.Trim().Trim('\''))
            .Select(file => file == "OFFLINE_URL" ? offlineUrl : file).ToList();
        cached.Should().Contain("/offline.html");
        foreach (var file in cached) File.Exists(Path.Combine(WwwRoot, file.TrimStart('/'))).Should().BeTrue(file);

        // The CSP has no 'unsafe-inline' for scripts: the offline page's script is a file.
        var offline = File.ReadAllText(Path.Combine(WwwRoot, "offline.html"));
        ScriptTag().Matches(offline).Should().OnlyContain(tag => tag.Value.Contains(" src=\""));
        cached.Should().Contain("/js/offline-retry.js");

        new FileExtensionContentTypeProvider().TryGetContentType("manifest.webmanifest", out var type).Should().BeTrue();
        type.Should().Be("application/manifest+json");
    }

    [Fact]
    public async Task ServiceWorker_ShouldTakeOverAdminPages_AndShowTheOfflinePage_WhenThereIsNoConnection()
    {
        using var server = new WwwRootServer(WwwRoot);
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{server.Origin}/admin/home");
        await page.WaitForFunctionAsync("navigator.serviceWorker.ready.then(() => true)", null, new() { Timeout = 15000 });
        // The first load isn't controlled yet (clients.claim takes over open pages).
        await page.WaitForFunctionAsync("!!navigator.serviceWorker.controller", null, new() { Timeout = 15000 });
        (await page.EvaluateAsync<string>("navigator.serviceWorker.controller.scriptURL")).Should().Be($"{server.Origin}/sw.js");

        // Offline: the browser's switch and, in case it doesn't reach the worker, no server either.
        await context.SetOfflineAsync(true);
        server.Stop();
        await page.GotoAsync($"{server.Origin}/admin/world-news");
        (await page.TextContentAsync("h1")).Should().Be("You're offline");
        (await page.EvaluateAsync<bool>("document.querySelector('main img').complete && document.querySelector('main img').naturalWidth > 0"))
            .Should().BeTrue("the icon comes from the worker's cache");
        (await page.EvaluateAsync<string>("location.pathname")).Should().Be("/admin/world-news", "a reload when back online retries the page itself");

        // Only page loads are answered: anything else fails like it would without the worker.
        (await page.EvaluateAsync<string>("fetch('/admin/api/anything').then(() => 'answered', () => 'failed')")).Should().Be("failed");

        // Back online, the offline page reloads itself (js/offline-retry.js) and gets the real page.
        server.Start();
        await context.SetOfflineAsync(false);
        await page.WaitForFunctionAsync("document.querySelector('h1')?.textContent === 'Admin page'", null, new() { Timeout = 15000 });
        (await page.EvaluateAsync<string>("location.pathname")).Should().Be("/admin/world-news");
    }

    [Fact]
    public async Task InstallOffer_ShouldExplainAddToHomeScreen_OnIPhoneSafari_Once()
    {
        using var server = new WwwRootServer(WwwRoot);
        await using var context = await fixture.Browser.NewContextAsync(new()
        {
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1",
            ViewportSize = new() { Width = 390, Height = 844 },
            IsMobile = true,
            HasTouch = true
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{server.Origin}/admin/home");
        await page.WaitForSelectorAsync("#gws-install-offer", new() { Timeout = 10000 });
        (await page.TextContentAsync("#gws-install-offer")).Should().Contain("Add to Home Screen").And.Contain("Got it");
        await page.ClickAsync("#gws-install-offer button:has-text('Got it')");
        (await page.Locator("#gws-install-offer").CountAsync()).Should().Be(0);

        await page.ReloadAsync();
        await page.WaitForTimeoutAsync(5000);
        (await page.Locator("#gws-install-offer").CountAsync()).Should().Be(0, "it's offered once");

        // Never on the public site.
        await page.GotoAsync($"{server.Origin}/public");
        await page.EvaluateAsync("localStorage.clear()");
        await page.ReloadAsync();
        await page.WaitForTimeoutAsync(5000);
        (await page.Locator("#gws-install-offer").CountAsync()).Should().Be(0);
        (await page.EvaluateAsync<int>("navigator.serviceWorker.getRegistrations().then(r => r.filter(x => x.scope.endsWith('/public')).length)")).Should().Be(0);
    }

    [Fact]
    public async Task ServiceWorker_ShouldShowAPushAsANotification_ThatOnlyEverOpensTheApp()
    {
        // sw.js's own handlers, run in a page with stand-ins for the worker's registration and
        // clients (a real push needs a push service, which a test browser hasn't got).
        using var server = new WwwRootServer(WwwRoot);
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Origin}/public");
        var worker = await File.ReadAllTextAsync(Path.Combine(WwwRoot, "sw.js"));
        await page.EvaluateAsync("""
            (source) => {
                window.__handlers = {};
                window.__shown = [];
                window.__opened = [];
                const add = window.addEventListener.bind(window);
                window.addEventListener = (type, handler) => { if (['install', 'activate', 'fetch', 'push', 'notificationclick', 'pushsubscriptionchange'].includes(type)) window.__handlers[type] = handler; else add(type, handler); };
                window.registration = { showNotification: (title, options) => { window.__shown.push({ title, body: options.body, tag: options.tag || null, url: options.data.url }); return Promise.resolve(); } };
                window.clients = { matchAll: () => Promise.resolve([]), openWindow: (url) => { window.__opened.push(url); return Promise.resolve(); } };
                (0, eval)(source);
            }
            """, worker);

        async Task<JsonElement> PushAsync(string? json, string? text = null) => await page.EvaluateAsync<JsonElement>("""
            async ([json, text]) => {
                let done;
                window.__handlers.push({
                    data: json === null && text === null ? null : { json: () => JSON.parse(json ?? text), text: () => text ?? json },
                    waitUntil: (promise) => { done = promise; }
                });
                await done;
                return window.__shown[window.__shown.length - 1];
            }
            """, new[] { json, text });

        var first = await PushAsync("""{"title":"Messages: Ops","body":"Ana: Deploy done","url":"/admin/community/messages/1","tag":"chat:1"}""");
        first.GetProperty("title").GetString().Should().Be("Messages: Ops");
        first.GetProperty("body").GetString().Should().Be("Ana: Deploy done");
        first.GetProperty("tag").GetString().Should().Be("chat:1");
        first.GetProperty("url").GetString().Should().Be("/admin/community/messages/1");

        // A push pointing anywhere else opens the app; one that isn't JSON still shows something.
        (await PushAsync("""{"title":"x","url":"https://evil.example/"}""")).GetProperty("url").GetString().Should().Be("/admin");
        var plain = await PushAsync(null, "hello");
        plain.GetProperty("title").GetString().Should().Be("GWS Business Suite");
        plain.GetProperty("body").GetString().Should().Be("hello");

        // Tapping it with no window open opens the page it's about.
        var opened = await page.EvaluateAsync<string>("""
            async () => {
                let done;
                window.__handlers.notificationclick({
                    notification: { close: () => {}, data: { url: '/admin/community/messages/1' } },
                    waitUntil: (promise) => { done = promise; }
                });
                await done;
                return window.__opened[0];
            }
            """);
        opened.Should().Be($"{server.Origin}/admin/community/messages/1");
    }

    private static (int Width, int Height) PngSize(string src)
    {
        var bytes = File.ReadAllBytes(Path.Combine(WwwRoot, src.TrimStart('/')));
        bytes.AsSpan(1, 3).SequenceEqual("PNG"u8).Should().BeTrue(src);
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [GeneratedRegex("""@page "([^"{]+)"\s""")]
    private static partial Regex PageRoute();

    [GeneratedRegex(@"const OFFLINE_URL = '([^']+)'")]
    private static partial Regex OfflineUrl();

    [GeneratedRegex(@"const OFFLINE_FILES = \[([^\]]+)\]")]
    private static partial Regex OfflineFiles();

    [GeneratedRegex(@"<script\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    // wwwroot over plain HTTP on localhost (a secure context, so service workers work). /admin/*
    // and /public answer with a small page that loads pwa.js the way App.razor does.
    private sealed class WwwRootServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _root;

        public string Origin { get; }

        public WwwRootServer(string root)
        {
            _root = root;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Origin = $"http://localhost:{port}";
            _listener.Prefixes.Add(Origin + "/");
            Start();
        }

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        // Refuses connections until Start, on the same origin (the service worker's).
        public void Stop() => _listener.Stop();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                try
                {
                    Respond(context);
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                    // The browser went away mid-response.
                }
            }
        }

        private void Respond(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            byte[] body;
            string type;
            if (path.StartsWith("/admin", StringComparison.Ordinal) || path == "/public")
            {
                body = """
                    <!DOCTYPE html><html><head><link rel="manifest" href="/manifest.webmanifest" /></head>
                    <body><h1>Admin page</h1><script src="/js/pwa.js"></script></body></html>
                    """u8.ToArray();
                type = "text/html; charset=utf-8";
            }
            else
            {
                var file = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/')));
                if (!file.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(file))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                body = File.ReadAllBytes(file);
                type = new FileExtensionContentTypeProvider().TryGetContentType(file, out var known) ? known : "application/octet-stream";
            }
            context.Response.ContentType = type;
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body);
            context.Response.Close();
        }

        public void Dispose() => _listener.Close();
    }
}
