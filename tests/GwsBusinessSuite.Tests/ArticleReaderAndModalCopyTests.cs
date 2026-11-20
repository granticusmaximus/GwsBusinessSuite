using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Media Watch's reader and copying from any modal (20-11-26, Grant).
public sealed class ArticleReaderTests
{
    private static readonly string Paragraphs = string.Concat(Enumerable.Range(1, 8).Select(i =>
        $"<p>Paragraph {i} of the story explains what happened in the city council meeting on Tuesday evening, who spoke, and what was decided about the new transit plan.</p>"));

    private static string Page(string body) => $$"""
        <!doctype html><html><head><title>Council approves transit plan | Example News</title>
        <meta property="og:site_name" content="Example News"><script>window.tracker = 1;</script></head>
        <body><nav><a href="/">Home</a><a href="/sports">Sports</a></nav>
        <article><h1>Council approves transit plan</h1><p class="byline">By Jane Reporter</p>
        {{body}}
        <img src="/photo.jpg" alt="The council chamber" onerror="alert(1)">
        <p><a href="javascript:alert(1)">bad link</a> and <a href="/more">more</a></p>
        </article><footer>Copyright</footer></body></html>
        """;

    [Fact]
    public void Read_ShouldKeepTheArticle_AsSafeFormattedHtml()
    {
        var article = ArticleReaderService.Read(new Uri("https://news.example.com/2026/council"), Page(Paragraphs), "Fallback");

        article.Readable.Should().BeTrue();
        article.Title.Should().Contain("Council approves transit plan");
        article.Html.Should().Contain("<p>Paragraph 1 of the story").And.Contain("https://news.example.com/photo.jpg");
        article.Html.Should().NotContain("<script").And.NotContain("onerror").And.NotContain("javascript:").And.NotContain("Sports");
        article.Minutes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Read_ShouldSayWhy_WhenThePageHasNoArticleText()
    {
        var article = ArticleReaderService.Read(new Uri("https://news.example.com/x"), Page("<p>Subscribe to read.</p>"), "Fallback title");

        article.Readable.Should().BeFalse();
        article.Html.Should().BeEmpty();
        article.Problem.Should().Contain("Open the original");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "doesn't let this server read")]
    [InlineData(HttpStatusCode.NotFound, "answered 404")]
    public async Task ReadAsync_ShouldRespectARefusal_AndExplainFailures(HttpStatusCode status, string expected)
    {
        var reader = new ArticleReaderService(new Factory(new Answer(status, "")), new MemoryCache(new MemoryCacheOptions()), NullLogger<ArticleReaderService>.Instance);

        var article = await reader.ReadAsync("https://news.example.com/a", "Title");

        article.Readable.Should().BeFalse();
        article.Problem.Should().Contain(expected);
    }

    [Fact]
    public async Task ReadAsync_ShouldFetchOnce_AndRefuseNonWebLinks()
    {
        var answer = new Answer(HttpStatusCode.OK, Page(Paragraphs));
        var reader = new ArticleReaderService(new Factory(answer), new MemoryCache(new MemoryCacheOptions()), NullLogger<ArticleReaderService>.Instance);

        (await reader.ReadAsync("https://news.example.com/b")).Readable.Should().BeTrue();
        (await reader.ReadAsync("https://news.example.com/b")).Readable.Should().BeTrue();
        answer.Calls.Should().Be(1, "a re-opened article comes from the cache");
        (await reader.ReadAsync("file:///etc/passwd")).Problem.Should().Contain("isn't a web address");
        answer.Calls.Should().Be(1);
    }

    private sealed class Answer(HttpStatusCode status, string html) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

[Collection("Playwright")]
public sealed class ModalCopyBrowserTests(PlaywrightBrowserFixture fixture)
{
    private static string AppScript => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/GwsBusinessSuite.Web/wwwroot/app.js")));

    [Fact]
    public async Task EveryModal_ShouldGetACopyButton_ThatCopiesItsText_AndADraggedSelectionShouldNotCloseIt()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("""
            <!doctype html><html><body>
            <div class="modal d-block" id="backdrop" style="position:fixed;inset:0">
              <div class="modal-dialog" style="margin:40px;width:400px"><div class="modal-content">
                <div class="modal-header"><h2 class="modal-title">Title</h2><button class="btn-close" aria-label="Close"></button></div>
                <div class="modal-body"><p id="text">Copy this sentence from the modal.</p></div>
              </div></div>
            </div>
            </body></html>
            """);
        await page.EvaluateAsync("""
            () => {
              window.__copied = [];
              Object.defineProperty(navigator, 'clipboard', { value: { writeText: t => { window.__copied.push(t); return Promise.resolve(); } } });
              Object.defineProperty(window, 'isSecureContext', { value: true });
              window.__closed = 0;
              document.getElementById('backdrop').addEventListener('click', e => { if (e.target.id === 'backdrop') window.__closed++; });
            }
            """);
        await page.AddScriptTagAsync(new() { Content = AppScript });

        await page.WaitForSelectorAsync(".modal-header > .gws-copy-button");
        await page.ClickAsync(".gws-copy-button");
        await page.WaitForFunctionAsync("() => window.__copied.length === 1");
        (await page.EvaluateAsync<string>("() => window.__copied[0]")).Should().Be("Copy this sentence from the modal.");

        // A panel added later (an Overwatch panel) gets one too.
        await page.EvaluateAsync("""
            () => document.body.insertAdjacentHTML('beforeend',
              '<div class="tg-stream-panel"><div class="tg-stream-panel-header"><span>PANEL</span><button>X</button></div><div class="tg-stream-panel-body">Panel facts</div></div>')
            """);
        await page.WaitForSelectorAsync(".tg-stream-panel-header > .gws-copy-button");

        // Selecting text and releasing over the backdrop doesn't close the modal.
        var box = (await page.Locator("#text").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(box.X + 2, box.Y + box.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + box.Width + 300, box.Y + 300, new() { Steps = 6 });
        await page.Mouse.UpAsync();
        (await page.EvaluateAsync<string>("() => String(window.getSelection())")).Should().NotBeEmpty();
        (await page.EvaluateAsync<int>("() => window.__closed")).Should().Be(0);

        // A plain click on the backdrop still closes it.
        await page.EvaluateAsync("() => window.getSelection().removeAllRanges()");
        await page.Mouse.ClickAsync(700, 500);
        (await page.EvaluateAsync<int>("() => window.__closed")).Should().Be(1);
    }
}
