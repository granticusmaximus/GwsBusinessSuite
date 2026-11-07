using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Infrastructure.Services;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GwsBusinessSuite.Tests;

// The web panel's SentinelGPT on a phone or tablet (PWA phase 3, 07-11-26): attached files and
// Deep Analysis worked out on the server.
public sealed class AssistantParityTests
{
    [Fact]
    public void Attachments_ShouldReadTextAndPdfs_CapLongOnes_AndTurnAwayEverythingElse()
    {
        var (notes, problem) = AssistantAttachments.Read("/Users/x/notes.md", Encoding.UTF8.GetBytes("﻿# Launch\r\n- Book the venue\r\n"));
        problem.Should().BeNull();
        notes!.Name.Should().Be("notes.md");
        notes.Text.Should().Be("# Launch\n- Book the venue");
        notes.Cut.Should().BeFalse();

        var (log, _) = AssistantAttachments.Read("big.log", Encoding.UTF8.GetBytes(new string('x', AssistantAttachments.MaxCharactersPerFile + 50)));
        log!.Cut.Should().BeTrue();
        log.Text.Should().HaveLength(AssistantAttachments.MaxCharactersPerFile);

        AssistantAttachments.Read("photo.txt", [0xFF, 0xD8, 0, 0, 0, 0, 1, 2]).Problem.Should().Contain("isn't text");
        AssistantAttachments.Read("app.exe", [1, 2, 3]).Problem.Should().Contain("text files or PDFs");
        AssistantAttachments.Read("broken.pdf", Encoding.ASCII.GetBytes("not a pdf")).Problem.Should().Contain("couldn't be read");
        AssistantAttachments.Read("huge.txt", new byte[AssistantAttachments.MaxBytes + 1]).Problem.Should().Contain("over 5 MB");

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Quarterly budget 4200", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        var (pdf, pdfProblem) = AssistantAttachments.Read("budget.pdf", builder.Build());
        pdfProblem.Should().BeNull();
        pdf!.Text.Should().Contain("Quarterly budget 4200");

        AssistantAttachments.Format([notes, pdf])!.Replace("\r\n", "\n").Should()
            .Be("--- notes.md ---\n# Launch\n- Book the venue\n--- budget.pdf ---\n" + pdf.Text + "\n", "each file under its name");
        AssistantAttachments.Format([]).Should().BeNull();
        AssistantAttachments.Accept.Should().Contain(".pdf").And.Contain(".md");
    }

    [Fact]
    public async Task DeepAnalysis_ShouldAskEachInstalledAdviser_AndGoOnWithoutOneThatFails()
    {
        DeepAnalysisAdvice.Advisers(["sentinelgpt:latest", "qwen2.5-coder:7b", "llama3.2"]).Select(a => a.Installed).Should().Equal("qwen2.5-coder:7b");
        DeepAnalysisAdvice.Advisers(["qwen2.5"]).Should().BeEmpty("qwen2.5 isn't the coder model");

        var models = new FakeModels(model => model.StartsWith("deepseek", StringComparison.Ordinal)
            ? throw new HttpRequestException("model went away")
            : "Check the board exists before adding the card.");
        var steps = new List<string>();
        var advice = await DeepAnalysisAdvice.BuildAsync(models, ["sentinelgpt:latest", "qwen2.5-coder:latest", "deepseek-r1:latest"],
            "Add a card", text => { steps.Add(text); return Task.CompletedTask; }, CancellationToken.None);

        advice.Should().StartWith("SPECIALIST ADVISORY - UNTRUSTED MODEL OPINIONS").And.Contain("QWEN ENGINEERING REVIEW:\nCheck the board exists")
            .And.NotContain("DEEPSEEK");
        steps.Should().Equal("Deep Analysis: asking qwen2.5-coder:latest for a second opinion", "Deep Analysis: asking deepseek-r1:latest for a second opinion");
        models.Asked.Should().Equal("qwen2.5-coder:latest", "deepseek-r1:latest");

        (await DeepAnalysisAdvice.BuildAsync(models, ["sentinelgpt:latest"], "x", _ => Task.CompletedTask, CancellationToken.None))
            .Should().BeNull("no adviser installed");
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        await FluentActions.Invoking(() => DeepAnalysisAdvice.BuildAsync(new FakeModels(_ => throw new OperationCanceledException()),
                ["qwen2.5-coder:latest"], "x", _ => Task.CompletedTask, stopped.Token))
            .Should().ThrowAsync<OperationCanceledException>("stopping the turn stops the advisers too");
    }

    private sealed class FakeModels(Func<string, string> answer) : IOllamaService
    {
        public List<string> Asked { get; } = [];

        public Task<OllamaChatResponse> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools = null,
            CancellationToken ct = default)
        {
            Asked.Add(model);
            return Task.FromResult(new OllamaChatResponse(answer(model), []));
        }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
