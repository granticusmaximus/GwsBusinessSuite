using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

// Inline writing assistance for the Sentinel editor's selection toolbar. Almost all of the risk
// here is in CleanResponse: a local model routinely ignores "return only the text", and whatever
// survives this method gets dropped straight into the user's page. Getting it wrong doesn't
// throw - it quietly pastes 'Here is the rewritten text:' into a document.
public sealed class SentinelWritingAssistantTests
{
    private static SentinelWritingAction Action(string key) =>
        SentinelWritingActions.Find(key) ?? throw new InvalidOperationException($"missing action {key}");

    [Fact]
    public void Catalog_ShouldExposeStableKeys()
    {
        // The editor sends these keys back verbatim, so renaming one silently breaks the menu.
        SentinelWritingActions.All.Select(action => action.Key)
            .Should().BeEquivalentTo(["improve", "shorten", "lengthen", "fix", "simplify", "professional", "continue"]);
    }

    [Fact]
    public void Catalog_ShouldOnlyAllowGrowthWhereItIsThePoint()
    {
        SentinelWritingActions.All.Where(action => action.AllowsGrowth).Select(action => action.Key)
            .Should().BeEquivalentTo(["lengthen", "continue"]);
    }

    [Fact]
    public void Find_ShouldBeCaseInsensitive_AndRejectUnknownKeys()
    {
        SentinelWritingActions.Find("IMPROVE").Should().NotBeNull();
        SentinelWritingActions.Find("delete-everything").Should().BeNull();
        SentinelWritingActions.Find(null).Should().BeNull();
        SentinelWritingActions.Find("  ").Should().BeNull();
    }

    [Fact]
    public void BuildUserPrompt_ShouldCarryTheSelectionAndTheCatalogInstruction()
    {
        var prompt = SentinelWritingAssistant.BuildUserPrompt(Action("fix"), "teh cat sat");

        prompt.Should().Contain("teh cat sat");
        prompt.Should().Contain(Action("fix").Instruction);
    }

    [Theory]
    [InlineData("Here is the rewritten text:\nThe cat sat.", "The cat sat.")]
    [InlineData("Sure! Here's the result:\nThe cat sat.", "The cat sat.")]
    [InlineData("```\nThe cat sat.\n```", "The cat sat.")]
    [InlineData("```text\nThe cat sat.\n```", "The cat sat.")]
    [InlineData("\"The cat sat.\"", "The cat sat.")]
    [InlineData("  The cat sat.  ", "The cat sat.")]
    public void CleanResponse_ShouldStripModelPackaging(string raw, string expected)
    {
        SentinelWritingAssistant.CleanResponse(raw, Action("improve"), "the cat sat")
            .Should().Be(expected);
    }

    [Fact]
    public void CleanResponse_ShouldDropReasoningPreamble_BeforeAnythingElseParsesIt()
    {
        // A <think> block can itself contain fences and lead-in lines, which is exactly why it
        // has to come off first. If the order regressed, the fence stripper would run against
        // the reasoning text and this would return the model's private deliberation.
        const string raw = "<think>```\nHere is my plan:\nrewrite it\n```</think>\nThe cat sat.";

        SentinelWritingAssistant.CleanResponse(raw, Action("improve"), "the cat sat")
            .Should().Be("The cat sat.");
    }

    [Fact]
    public void CleanResponse_ShouldKeepAGenuineFirstLineEndingInAColon()
    {
        // "Ingredients:" is content, not packaging. Eating it would silently lose the user's
        // own words, which is worse than leaving a stray lead-in behind.
        const string raw = "Ingredients:\nFlour and water.";

        SentinelWritingAssistant.CleanResponse(raw, Action("improve"), "flour and water, listed")
            .Should().Be(raw);
    }

    [Fact]
    public void CleanResponse_ShouldKeepInnerQuotes_WhenTheyAreNotAWrapper()
    {
        const string raw = "\"Stop,\" he said, \"now.\"";

        SentinelWritingAssistant.CleanResponse(raw, Action("improve"), "he told them to stop")
            .Should().Be(raw);
    }

    [Fact]
    public void CleanResponse_ShouldRejectARunawayRewrite()
    {
        // A model that answers one sentence with an essay has ignored the task. Returning empty
        // makes that a visible failure; truncating would paste a fragment the user wouldn't spot.
        var runaway = string.Join(" ", Enumerable.Repeat("padding", 400));

        SentinelWritingAssistant.CleanResponse(runaway, Action("improve"), "short input")
            .Should().BeEmpty();
    }

    [Fact]
    public void CleanResponse_ShouldGiveGrowthActionsMoreHeadroom()
    {
        var original = new string('x', 200);
        var expanded = new string('y', 900);

        SentinelWritingAssistant.CleanResponse(expanded, Action("continue"), original).Should().NotBeEmpty();
        SentinelWritingAssistant.CleanResponse(expanded, Action("improve"), original).Should().BeEmpty();
    }

    [Fact]
    public async Task TransformAsync_ShouldRejectAnUnknownAction_WithoutCallingTheModel()
    {
        var ollama = new RecordingOllamaService { Response = "should never be used" };
        var service = new SentinelWritingAssistantService(ollama, new FakeSiteSettings());

        var result = await service.TransformAsync("rm-rf", "hello");

        result.Succeeded.Should().BeFalse();
        ollama.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TransformAsync_ShouldRejectAnOversizedSelection_WithoutCallingTheModel()
    {
        var ollama = new RecordingOllamaService { Response = "x" };
        var service = new SentinelWritingAssistantService(ollama, new FakeSiteSettings());

        var result = await service.TransformAsync(
            "improve",
            new string('x', SentinelWritingAssistant.MaxSelectionLength + 1));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("too long");
        ollama.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TransformAsync_ShouldUseTheConfiguredModelOverride()
    {
        var ollama = new RecordingOllamaService { Response = "Rewritten." };
        var service = new SentinelWritingAssistantService(
            ollama,
            new FakeSiteSettings { OllamaModelOverride = "gemma4" });

        var result = await service.TransformAsync("improve", "rewrite me");

        result.Succeeded.Should().BeTrue();
        result.Text.Should().Be("Rewritten.");
        ollama.Calls.Single().Model.Should().Be("gemma4");
    }

    [Fact]
    public async Task TransformAsync_ShouldFallBackToTheDefaultModel_WhenNoOverrideIsSet()
    {
        var ollama = new RecordingOllamaService { Response = "Rewritten." };
        var service = new SentinelWritingAssistantService(ollama, new FakeSiteSettings());

        await service.TransformAsync("improve", "rewrite me");

        ollama.Calls.Single().Model.Should().Be(SentinelGptDefaults.Model);
    }

    [Fact]
    public async Task TransformAsync_ShouldReportFailure_WhenTheModelIsUnreachable()
    {
        var service = new SentinelWritingAssistantService(
            new ThrowingOllamaService(),
            new FakeSiteSettings());

        var result = await service.TransformAsync("improve", "rewrite me");

        result.Succeeded.Should().BeFalse();
        result.Text.Should().BeEmpty();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TransformAsync_ShouldReportFailure_WhenTheModelReturnsNothingUsable()
    {
        var service = new SentinelWritingAssistantService(
            new RecordingOllamaService { Response = "   " },
            new FakeSiteSettings());

        var result = await service.TransformAsync("improve", "rewrite me");

        result.Succeeded.Should().BeFalse();
    }

    private sealed class FakeSiteSettings : ISiteSettingsService
    {
        public string? OllamaModelOverride { get; init; }

        public Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteSettingsView(10, null, null, OllamaModelOverride, null, null, 10));

        public Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingOllamaService : IOllamaService
    {
        public string Response { get; set; } = string.Empty;
        public List<(string Model, string SystemPrompt, string UserPrompt)> Calls { get; } = [];

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            Calls.Add((model, systemPrompt, userPrompt));
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class ThrowingOllamaService : IOllamaService
    {
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
