using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// Mac first, AI droplet second (13-11-26).
public sealed class OllamaFailoverTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static OllamaFailoverMonitor Monitor(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) =>
        new(new Clients(respond ?? (_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") })),
            new OllamaWorkloadScheduler(), NullLogger<OllamaFailoverMonitor>.Instance);

    private static FailoverOllamaService Service(Fake mac, Fake droplet, OllamaFailoverMonitor monitor) =>
        new(mac, droplet, monitor, NullLogger<FailoverOllamaService>.Instance);

    [Fact]
    public void Circuit_ShouldGoDownAfterTwoMisses_AndComeBackAfterThreeAnswers()
    {
        var state = OllamaFailoverMonitor.CircuitState.Initial;
        state = state.After(false, At); state.Up.Should().BeTrue("one miss is a blip");
        state = state.After(false, At); state.Up.Should().BeFalse();
        state = state.After(true, At).After(true, At); state.Up.Should().BeFalse("not yet steady");
        state = state.After(false, At).After(true, At).After(true, At); state.Up.Should().BeFalse("a miss restarts the count");
        state = state.After(true, At); state.Up.Should().BeTrue();
    }

    [Fact]
    public async Task AConnectionFailureOnTheMac_ShouldAnswerFromTheDroplet_AndMarkTheMacDown()
    {
        using var monitor = Monitor();
        var lost = monitor.PrimaryLostToken;
        var service = Service(new Fake("mac", new HttpRequestException("refused")), new Fake("droplet"), monitor);

        (await service.GenerateAsync("sentinelgpt", "s", "u")).Should().Be("droplet");
        monitor.PrimaryUp.Should().BeFalse();
        lost.IsCancellationRequested.Should().BeTrue("requests still waiting on the Mac move too");
        (await service.GenerateAsync("sentinelgpt", "s", "u")).Should().Be("droplet", "the Mac isn't asked while it's down");
    }

    [Fact]
    public async Task AnErrorAnswerFromAMacThatsThere_ShouldUseTheDroplet_WithoutMarkingTheMacDown()
    {
        using var monitor = Monitor();
        var service = Service(new Fake("mac", new InvalidOperationException("model not found")), new Fake("droplet"), monitor);

        (await service.GenerateAsync("qwen3:14b", "s", "u")).Should().Be("droplet");
        monitor.PrimaryUp.Should().BeTrue();
    }

    [Fact]
    public async Task TheCallersOwnCancellation_ShouldNotFallBack()
    {
        using var monitor = Monitor();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var droplet = new Fake("droplet");
        var service = Service(new Fake("mac", new OperationCanceledException()), droplet, monitor);

        await FluentActions.Awaiting(() => service.GenerateAsync("m", "s", "u", cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        droplet.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Streams_ShouldFallBackBeforeTheFirstWord_ButNotAfter()
    {
        using var monitor = Monitor();
        var before = Service(new Fake("mac", new HttpRequestException("refused")), new Fake("droplet"), monitor);
        (await Collect(before.GenerateStreamAsync("m", "s", "u"))).Should().Equal("droplet");

        using var monitor2 = Monitor();
        var after = Service(new Fake("mac", new HttpRequestException("gone"), failAfterFirstWord: true), new Fake("droplet"), monitor2);
        await FluentActions.Awaiting(() => Collect(after.GenerateStreamAsync("m", "s", "u")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage(FailoverOllamaService.MidAnswerMessage);
    }

    [Fact]
    public async Task Embeddings_ShouldStayOnTheDroplet_AndOnlyMoveToAMacWithTheSameModel()
    {
        using var monitor = Monitor();
        var mac = new Fake("mac");
        var service = Service(mac, new Fake("droplet", new HttpRequestException("down")), monitor);

        await FluentActions.Awaiting(() => service.EmbedAsync("embeddinggemma", ["a"])).Should().ThrowAsync<HttpRequestException>("the Mac's build differs");
        mac.Calls.Should().Be(0);

        using var same = Monitor(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"models":[{"name":"embeddinggemma:latest","digest":"abc"}]}""") });
        await same.ProbeOnceAsync(CancellationToken.None);
        same.SameModelOnBoth("embeddinggemma").Should().BeTrue();
        (await Service(new Fake("mac"), new Fake("droplet", new HttpRequestException("down")), same).EmbedAsync("embeddinggemma", ["a"]))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Probes_ShouldMarkAQuietMacDownAndBack()
    {
        var macAnswers = false;
        using var monitor = Monitor(request => request.RequestUri!.Host == "mac" && !macAnswers
            ? throw new HttpRequestException("timeout")
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") });

        await monitor.ProbeOnceAsync(CancellationToken.None);
        await monitor.ProbeOnceAsync(CancellationToken.None);
        monitor.PrimaryUp.Should().BeFalse();
        macAnswers = true;
        for (var i = 0; i < OllamaFailoverMonitor.UpAfterAnswers; i++) await monitor.ProbeOnceAsync(CancellationToken.None);
        monitor.PrimaryUp.Should().BeTrue();
        monitor.PrimaryLostToken.IsCancellationRequested.Should().BeFalse("a fresh token once it's back");
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
    {
        var pieces = new List<string>();
        await foreach (var piece in stream) pieces.Add(piece);
        return pieces;
    }

    private sealed class Clients(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(respond))
            { BaseAddress = new Uri(name == OllamaFailoverMonitor.PrimaryClient ? "http://mac/" : "http://droplet/") };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Fake(string name, Exception? fail = null, bool failAfterFirstWord = false) : IOllamaService
    {
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            Calls++;
            return fail is null ? Task.FromResult(name) : Task.FromException<string>(fail);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await Task.Yield();
            if (fail is not null && !failAfterFirstWord) throw fail;
            yield return name;
            if (fail is not null) throw fail;
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            Calls++;
            return fail is null ? Task.FromResult<IReadOnlyList<float[]>>([[1f]]) : Task.FromException<IReadOnlyList<float[]>>(fail);
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([name]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => Task.FromResult(name);
    }
}
