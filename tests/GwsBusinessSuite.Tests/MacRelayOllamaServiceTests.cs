using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// 05-11-26: with server AI off (31-10-26) bill overviews, news hot takes and county meeting
// summaries had stopped - about 1,400 warnings a day. Shared work on public data now runs on an
// Admin's signed-in Mac through SentinelGPT's agent relay; everything else still fails closed.
public sealed class MacRelayOllamaServiceTests
{
    private static readonly string[] GrantsModels = ["embeddinggemma:latest", "sentinelgpt:latest"];

    [Fact]
    public async Task SharedWork_ShouldRunOnTheAdminsMac_WithTheModelAskedFor()
    {
        var relay = new SentinelAgentRelay(TimeProvider.System);
        var service = new MacRelayOllamaService(relay);
        var poll = relay.TakeAsync("grant", GrantsModels, default, takesSharedWork: true);

        Task<string> answer;
        using (MacRelayScope.ForSharedWork())
        {
            answer = service.GenerateAsync("sentinelgpt", "Summarize the bill.", "HB 1: road funding");
        }
        var call = (await poll)!.Value;
        var payload = JsonNode.Parse(call.PayloadJson)!;
        payload["model"]!.GetValue<string>().Should().Be("sentinelgpt:latest");
        payload["messages"]!.AsArray().Select(m => (m!["role"]!.GetValue<string>(), m["content"]!.GetValue<string>()))
            .Should().Equal(("system", "Summarize the bill."), ("user", "HB 1: road funding"));
        relay.Complete("grant", call.Id, TextReply("  It funds roads.  "), null).Should().BeTrue();

        (await answer).Should().Be("It funds roads.");
    }

    [Fact]
    public async Task EverythingElse_ShouldStillFailClosed_EvenWithAnAdminsMacOnline()
    {
        var relay = new SentinelAgentRelay(TimeProvider.System);
        var service = new MacRelayOllamaService(relay);
        using var stop = new CancellationTokenSource();
        var poll = relay.TakeAsync("grant", GrantsModels, stop.Token, takesSharedWork: true);

        // Outside the scope: someone's own request never goes to another person's Mac.
        await service.Invoking(s => s.GenerateAsync("sentinelgpt", "system", "my private draft"))
            .Should().ThrowAsync<ServerAiUnavailableException>();
        using (MacRelayScope.ForSharedWork())
        {
            await service.Invoking(s => s.EmbedAsync("embeddinggemma", ["x"]))
                .Should().ThrowAsync<ServerAiUnavailableException>("the agent relay carries chat only");
        }
        poll.IsCompleted.Should().BeFalse("nothing was handed to the Mac");
        stop.Cancel();
        await poll.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SharedWork_WithNoAdminMacOnline_ShouldFailAtOnce_AsUnavailable()
    {
        var relay = new SentinelAgentRelay(TimeProvider.System);
        var service = new MacRelayOllamaService(relay);
        using var stop = new CancellationTokenSource();
        // A Mac signed in as a non-admin doesn't take shared work.
        var writersPoll = relay.TakeAsync("writer", GrantsModels, stop.Token);

        using (MacRelayScope.ForSharedWork())
        {
            var started = DateTimeOffset.UtcNow;
            (await service.Invoking(s => s.GenerateAsync("sentinelgpt", "system", "bill")).Should().ThrowAsync<ServerAiUnavailableException>())
                .Which.Message.Should().Contain("signed in as an admin");
            (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "it doesn't wait for a Mac that isn't there");
        }
        relay.SharedWorkMac().Should().BeNull();
        await service.Invoking(s => relay.ChatSharedAsync("writer", "{}", default)).Should().ThrowAsync<InvalidOperationException>();
        stop.Cancel();
        await writersPoll.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ThePersonsOwnSteps_ShouldGoBeforeSharedWork_OnTheirMac()
    {
        var relay = new SentinelAgentRelay(TimeProvider.System);
        var first = relay.TakeAsync("grant", GrantsModels, default, takesSharedWork: true);
        var bill = relay.ChatSharedAsync("grant", "bill", default);
        var firstCall = (await first)!.Value;
        firstCall.PayloadJson.Should().Be("bill");

        // Queued while the Mac works on the bill: another shared job, then one of Grant's own steps.
        var news = relay.ChatSharedAsync("grant", "news", default);
        var own = relay.ChatAsync("grant", "own step", default);
        relay.Complete("grant", firstCall.Id, TextReply("bill done"), null);
        (await bill).Should().Contain("bill done");

        var next = (await relay.TakeAsync("grant", GrantsModels, default, takesSharedWork: true))!.Value;
        next.PayloadJson.Should().Be("own step", "the person's own step jumps the shared queue");
        relay.Complete("grant", next.Id, TextReply("ok"), null);
        var last = (await relay.TakeAsync("grant", GrantsModels, default, takesSharedWork: true))!.Value;
        last.PayloadJson.Should().Be("news");
        relay.Complete("grant", last.Id, TextReply("news done"), null);
        await Task.WhenAll(own, news);
    }

    [Theory]
    [InlineData("sentinelgpt", "sentinelgpt:latest")]
    [InlineData("gemma4:latest", "gemma4:latest")]
    public void TheModelAskedFor_ShouldBeUsed_WhenTheMacHasIt(string asked, string expected) =>
        MacRelayOllamaService.ModelOn(["embeddinggemma:latest", "gemma4:latest", "sentinelgpt:latest"], asked).Should().Be(expected);

    [Fact]
    public void AMacWithOnlyAnEmbeddingModel_ShouldHaveNoModelForSharedWork() =>
        MacRelayOllamaService.ModelOn(["embeddinggemma:latest"], "sentinelgpt").Should().BeNull();

    private static string TextReply(string text) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = text } });
}
