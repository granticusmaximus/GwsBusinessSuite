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
        using var route = OllamaComputeRouting.Use(OllamaComputeRoute.PreferMac);
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
        using var route = OllamaComputeRouting.Use(OllamaComputeRoute.PreferMac);
        using var monitor = Monitor();
        var service = Service(new Fake("mac", new InvalidOperationException("model not found")), new Fake("droplet"), monitor);

        (await service.GenerateAsync("qwen3:14b", "s", "u")).Should().Be("droplet");
        monitor.PrimaryUp.Should().BeTrue();
    }

    [Fact]
    public async Task TheCallersOwnCancellation_ShouldNotFallBack()
    {
        using var route = OllamaComputeRouting.Use(OllamaComputeRoute.PreferMac);
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
        using var route = OllamaComputeRouting.Use(OllamaComputeRoute.PreferMac);
        using var monitor = Monitor();
        var before = Service(new Fake("mac", new HttpRequestException("refused")), new Fake("droplet"), monitor);
        (await Collect(before.GenerateStreamAsync("m", "s", "u"))).Should().Equal("droplet");

        using var monitor2 = Monitor();
        var after = Service(new Fake("mac", new HttpRequestException("gone"), failAfterFirstWord: true), new Fake("droplet"), monitor2);
        await FluentActions.Awaiting(() => Collect(after.GenerateStreamAsync("m", "s", "u")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage(FailoverOllamaService.MidAnswerMessage);
    }

    [Fact]
    // 19-11-26 (Grant): the Mac does the work and the droplet is only the fallback, short requests included.
    public async Task ShortRequests_ShouldGoToTheMacFirst_AndOnlyFallBackToTheDroplet()
    {
        using var monitor = Monitor();
        var mac = new Fake("mac");
        var droplet = new Fake("droplet");
        var service = Service(mac, droplet, monitor);

        (await service.GenerateAsync("sentinelgpt", "short", "question")).Should().Be("mac");
        mac.Calls.Should().Be(1);
        droplet.Calls.Should().Be(0);

        using (OllamaComputeRouting.Use(OllamaComputeRoute.PreferDroplet))
            (await service.GenerateAsync("sentinelgpt", "short", "question")).Should().Be("droplet", "a caller can still ask for the droplet");

        using var monitor2 = Monitor();
        var fallback = Service(new Fake("mac", new HttpRequestException("refused")), new Fake("droplet"), monitor2);
        (await fallback.GenerateAsync("sentinelgpt", "short", "question")).Should().Be("droplet");
    }

    [Fact]
    public async Task ExplicitMacRoute_ShouldOverrideARequestsSize()
    {
        using var monitor = Monitor();
        var mac = new Fake("mac");
        var droplet = new Fake("droplet");
        var service = Service(mac, droplet, monitor);

        using (OllamaComputeRouting.Use(OllamaComputeRoute.PreferMac))
            (await service.GenerateAsync("sentinelgpt", "short", "question")).Should().Be("mac");

        mac.Calls.Should().Be(1);
        droplet.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AStuckPreferredDroplet_ShouldTimeOutAndFallBackToTheMac()
    {
        using var monitor = Monitor();
        var mac = new Fake("mac");
        var droplet = new Fake("droplet", waitUntilCancelled: true);
        var service = Service(mac, droplet, monitor);

        using (OllamaComputeRouting.Use(OllamaComputeRoute.PreferDroplet, TimeSpan.FromMilliseconds(25)))
            (await service.GenerateAsync("sentinelgpt", "short", "question")).Should().Be("mac");

        droplet.Calls.Should().Be(1);
        mac.Calls.Should().Be(1);
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

    // 19-11-26: the Mac's Tailscale had stopped and the droplet carried everything for hours with one
    // log line at the start. While the Mac stays unreachable the monitor says so again every hour.
    [Fact]
    public async Task AMacThatStaysDown_ShouldBeReportedAgainEveryHour()
    {
        var time = new MovableTime(At);
        var log = new WarningLog();
        using var monitor = new OllamaFailoverMonitor(new SplitClients(), new OllamaWorkloadScheduler(), log, time: time);

        for (var i = 0; i < OllamaFailoverMonitor.DownAfterMisses; i++) await monitor.ProbeOnceAsync(CancellationToken.None);
        monitor.PrimaryUp.Should().BeFalse();
        log.Reminders.Should().Be(0, "the drop itself has its own warning");

        time.Advance(TimeSpan.FromMinutes(61));
        await monitor.ProbeOnceAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(30));
        await monitor.ProbeOnceAsync(CancellationToken.None);
        log.Reminders.Should().Be(1, "once an hour, not every probe");

        time.Advance(TimeSpan.FromMinutes(31));
        await monitor.ProbeOnceAsync(CancellationToken.None);
        log.Reminders.Should().Be(2);
    }

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // The Mac's client fails to connect; the droplet's answers.
    private sealed class SplitClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Answer(name == OllamaFailoverMonitor.PrimaryClient))
            { BaseAddress = new Uri("http://ollama.test/") };

        private sealed class Answer(bool fail) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                fail ? throw new HttpRequestException("unreachable")
                    : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") });
        }
    }

    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger<OllamaFailoverMonitor>
    {
        public int Reminders { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning && formatter(state, exception).Contains("has been unreachable", StringComparison.Ordinal))
                Reminders++;
        }
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

    private sealed class Fake(string name, Exception? fail = null, bool failAfterFirstWord = false, bool waitUntilCancelled = false) : IOllamaService
    {
        public int Calls { get; private set; }

        public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            Calls++;
            if (waitUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (fail is not null) throw fail;
            return name;
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
