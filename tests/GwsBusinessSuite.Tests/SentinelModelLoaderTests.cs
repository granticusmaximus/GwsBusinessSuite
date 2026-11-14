using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

// Lazy model loading: load only what isn't loaded, on the connection asked, at most every 2 minutes.
public sealed class SentinelModelLoaderTests
{
    private sealed class FakeOllama
    {
        public string Tags = """{"models":[{"name":"sentinelgpt:latest"},{"name":"llama3.2:latest"}]}""";
        public string Loaded = """{"models":[]}""";
        public List<string> Requests { get; } = [];

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}" + (request.Content is { } c ? " " + c.ReadAsStringAsync().Result : ""));
            var body = path switch { "/api/tags" => Tags, "/api/ps" => Loaded, _ => "{}" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Clients(FakeOllama ollama) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(ollama.Respond)) { BaseAddress = new Uri($"http://{name}.test/") };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private static SentinelModelLoader Loader(FakeOllama ollama, ServerAiMode mode = ServerAiMode.Full) => new(
        new Clients(ollama), new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        NullLogger<SentinelModelLoader>.Instance, Options.Create(new ServerAiOptions { Mode = mode }),
        Options.Create(new ContentStudioOptions { Model = "llama3.2" }));

    [Fact]
    public async Task Load_ShouldLoadAModelThatIsntLoaded_WithAKeepAlive()
    {
        var ollama = new FakeOllama();

        var result = await Loader(ollama).LoadAsync("ollama-primary", "sentinelgpt", null, CancellationToken.None);

        Assert.Equal("loaded", result);
        var load = Assert.Single(ollama.Requests, r => r.StartsWith("POST /api/generate", StringComparison.Ordinal));
        Assert.Contains("\"model\":\"sentinelgpt:latest\"", load);
        Assert.Contains("\"keep_alive\":\"30m\"", load);
        Assert.DoesNotContain("prompt", load);
    }

    [Fact]
    public async Task Load_ShouldDoNothingMore_WhenTheModelIsAlreadyLoaded_AndNotRecheckForTwoMinutes()
    {
        var ollama = new FakeOllama { Loaded = """{"models":[{"name":"sentinelgpt:latest"}]}""" };
        var loader = Loader(ollama);

        Assert.Equal("already loaded", await loader.LoadAsync("ollama-primary", "sentinelgpt", null, CancellationToken.None));
        Assert.Equal("recently checked", await loader.LoadAsync("ollama-primary", "sentinelgpt", null, CancellationToken.None));
        Assert.DoesNotContain(ollama.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
        Assert.Equal(2, ollama.Requests.Count);
    }

    [Fact]
    public async Task Load_ShouldUseTheServerDefault_WhenThisMachineHasntGotTheChosenModel()
    {
        var ollama = new FakeOllama();

        Assert.Equal("loaded", await Loader(ollama).LoadAsync("ollama-secondary", "gemma4-mac-only", "llama3.2", CancellationToken.None));
        Assert.Contains(ollama.Requests, r => r.Contains("\"model\":\"llama3.2:latest\"", StringComparison.Ordinal));
        Assert.Equal("not installed", await Loader(new FakeOllama()).LoadAsync("ollama-secondary", "gemma4-mac-only", null, CancellationToken.None));
    }

    [Fact]
    public void Loader_ShouldBeOff_WithNoServerModel()
    {
        var ollama = new FakeOllama();
        var loader = Loader(ollama, ServerAiMode.None);

        loader.SentinelOpened();

        Assert.False(loader.Enabled);
        Assert.Empty(ollama.Requests);
    }

    [Fact]
    public void Names_ShouldReadTagsAndPs() =>
        Assert.Equal(["a:latest", "b"], SentinelModelLoader.Names("""{"models":[{"name":"a:latest"},{"model":"b"}]}"""));
}
