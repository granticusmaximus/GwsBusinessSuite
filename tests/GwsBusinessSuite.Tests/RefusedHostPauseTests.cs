using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// 05-11-26: Eventbrite (405), gov.georgia.gov and senate.gov (403) refused every request from the
// droplet, about 450 times a day. A refusing site is now left alone for a day; nothing works around it.
public sealed class RefusedHostPauseTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Site(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(request.RequestUri!.Host == "fine.example" ? HttpStatusCode.OK : status));
        }
    }

    private static (HttpClient Client, Site Site, Clock Clock) Create(HttpStatusCode status)
    {
        var clock = new Clock();
        var site = new Site(status);
        var handler = new RefusedHostPauseHandler(new RefusedHostPause(clock), NullLogger<RefusedHostPauseHandler>.Instance) { InnerHandler = site };
        return (new HttpClient(handler), site, clock);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task ARefusingSite_ShouldBeLeftAloneForADay_AndOthersStillAsked(HttpStatusCode refusal)
    {
        var (client, site, clock) = Create(refusal);

        (await client.GetAsync("https://www.eventbrite.com/d/ga--macon/events/")).StatusCode.Should().Be(refusal);
        var again = () => client.GetAsync("https://www.eventbrite.com/d/ga--perry/events/");
        (await again.Should().ThrowAsync<HostPausedException>()).Which.Message.Should().Contain("06-11-26 12:00");
        site.Calls.Should().Be(1, "the paused site wasn't asked again");

        (await client.GetAsync("https://fine.example/feed")).StatusCode.Should().Be(HttpStatusCode.OK, "other sites are unaffected");

        clock.Now = clock.Now.Add(RefusedHostPause.Pause);
        (await client.GetAsync("https://www.eventbrite.com/d/ga--perry/events/")).StatusCode.Should().Be(refusal, "a day later it's asked again");
    }

    [Fact]
    public async Task ARequestWithAToken_ShouldNeverPauseItsSite_SinceTheTokenIsWhatWasRefused()
    {
        var (client, site, _) = Create(HttpStatusCode.Forbidden);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.gov/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "expired");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("https://api.example.gov/sessions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        site.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherFailures_ShouldNotPauseTheSite(HttpStatusCode status)
    {
        var (client, site, _) = Create(status);
        await client.GetAsync("https://www.senate.gov/a");
        await client.GetAsync("https://www.senate.gov/b");
        site.Calls.Should().Be(2);
    }
}
