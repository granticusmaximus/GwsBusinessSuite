using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.OllamaKit;
using GwsBusinessSuite.SentinelAgentKit;

namespace GwsBusinessSuite.Tests;

public sealed class OllamaKitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-ollamakit-{Guid.NewGuid():N}");

    public OllamaKitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ChatStreamAsync_YieldsContentDeltasThenADoneChunk()
    {
        var body = string.Join('\n',
            """{"message":{"content":"Hello"},"done":false}""",
            """{"message":{"content":" world"},"done":false}""",
            """{"message":{"content":""},"done":true}""");
        using var client = CreateClient(_ => NdjsonResponse(body));

        var chunks = new List<OllamaChatStreamChunk>();
        await foreach (var chunk in client.ChatStreamAsync(
            "llama3.2", [new OllamaChatMessage("user", "hi")], [], default))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(3);
        string.Concat(chunks.Take(2).Select(c => c.ContentDelta)).Should().Be("Hello world");
        chunks[^1].Done.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallingAgent_WithNoToolCalls_StreamsContentAndRecordsTheAssistantTurn()
    {
        var body = string.Join('\n',
            """{"message":{"content":"Paris"},"done":false}""",
            """{"message":{"content":" is the capital."},"done":false}""",
            """{"message":{"content":""},"done":true}""");
        using var client = CreateClient(_ => NdjsonResponse(body));
        var agent = new OllamaToolCallingAgent(client, new FakeToolExecutor([]), "llama3.2", "system prompt", maxRounds: 3);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("What is the capital of France?", default))
            events.Add(e);

        events.Should().OnlyContain(e => e.ToolActivity == null);
        string.Concat(events.Select(e => e.ContentDelta)).Should().Be("Paris is the capital.");
        agent.Messages.Should().HaveCount(3); // system, user, assistant
        agent.Messages[^1].Role.Should().Be("assistant");
        agent.Messages[^1].Content.Should().Be("Paris is the capital.");
    }

    [Fact]
    public async Task ToolCallingAgent_WithAToolCall_DispatchesToTheExecutorAndContinues()
    {
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round == 1
                ? NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"search_wiki","arguments":{"query":"deploy"}}}]},"done":true}""")
                : NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"Found it."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""));
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "qwen2.5-coder", "system prompt", maxRounds: 3);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("How do I deploy?", default))
            events.Add(e);

        executor.Calls.Should().ContainSingle(call => call.Name == "search_wiki");
        events.Should().Contain(e => e.ToolActivity == "search_wiki");
        string.Concat(events.Select(e => e.ContentDelta)).Should().Be("Found it.");
        agent.Messages.Should().Contain(m => m.Role == "tool" && m.ToolName == "search_wiki");
        agent.Messages[^1].Content.Should().Be("Found it.");
    }

    [Fact]
    public async Task ToolCallingAgent_WithAMalformedToolCallAttempt_GivesOneCorrectiveRoundInsteadOfShowingRawJson()
    {
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round == 1
                // Wrong field name ("parameters" not "arguments") - the exact real failure mode
                // observed from a local model attempting a tool call outside native tool_calls.
                ? NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"{\"name\":\"search_wiki\",\"parameters\":{\"query\":\"deploy\"}}"},"done":false}""",
                    """{"message":{"content":""},"done":true}"""))
                : NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"Here you go."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""));
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "qwen2.5-coder", "system prompt", maxRounds: 3);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("How do I deploy?", default))
            events.Add(e);

        executor.Calls.Should().BeEmpty("the malformed attempt never named a real, parseable tool call");
        events.Should().Contain(e => e.ToolActivity == "retrying");
        // The malformed JSON streamed as real Content events before it could be classified (that
        // can't be known until the round finishes) - what matters is that a ToolActivity event
        // came after it, which is the same "treat this as replaceable" signal a real tool call
        // uses, so a UI consumer resets its display rather than appending. Mirror that here:
        // only the events after the last ToolActivity should make up the real, final answer.
        var lastToolActivityIndex = events.FindLastIndex(e => e.ToolActivity is not null);
        lastToolActivityIndex.Should().BeGreaterThanOrEqualTo(0);
        string.Concat(events.Skip(lastToolActivityIndex + 1).Select(e => e.ContentDelta)).Should().Be("Here you go.");
        agent.Messages.Should().Contain(m => m.Role == "tool" && m.ToolName == "invalid_tool_call");
        agent.Messages[^1].Content.Should().Be("Here you go.");
    }

    [Theory]
    [InlineData("/skills", true)]
    [InlineData("/Skills", true)]
    [InlineData("/help me understand this", true)]
    // A message that merely opens with a path must stay a normal prompt - this is a developer's
    // assistant, so "/app/data is full" and "/usr/bin isn't on PATH" are ordinary things to type.
    [InlineData("/app/data is full, what should I prune?", false)]
    [InlineData("/ ", false)]
    [InlineData("/", false)]
    [InlineData("what does /skills do?", false)]
    [InlineData("/usr/bin isn't on PATH", false)]
    // A near miss is still a command attempt - it gets corrected, not forwarded to the model.
    [InlineData("/skill", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SlashCommands_TreatsOnlyRealCommandAttemptsAsCommands(string? input, bool expected) =>
        SlashCommands.LooksLikeCommand(input).Should().Be(expected);

    [Theory]
    [InlineData("/skills", "skills", "")]
    [InlineData("  /skills  ", "skills", "")]
    [InlineData("/SKILLS", "skills", "")]
    [InlineData("/agent reviewer", "agent", "reviewer")]
    // The argument keeps its own casing and inner spacing - it is usually a prompt for the model.
    [InlineData("/skills review Check THIS file for bugs", "skills", "review Check THIS file for bugs")]
    public void SlashCommands_SplitsTheNameFromItsArgument(string input, string name, string argument)
    {
        SlashCommands.TryParse(input, out var parsedName, out var parsedArgument).Should().BeTrue();
        parsedName.Should().Be(name);
        parsedArgument.Should().Be(argument);
    }

    [Fact]
    public void SlashCommands_HelpListsEveryCommandSoNoneCanBeSilentlyUndiscoverable()
    {
        // The whole point of the help text is discoverability; a command missing from it is
        // invisible, which is exactly the state /skills was in before it existed at all.
        var help = SlashCommands.BuildHelp();

        SlashCommands.All.Should().NotBeEmpty();
        foreach (var command in SlashCommands.All)
        {
            help.Should().Contain(command.Usage);
            help.Should().Contain(command.Description);
            SlashCommands.Find(command.Name).Should().BeSameAs(command);
        }
    }

    [Fact]
    public void SlashCommands_SuggestsTheIntendedCommandForANearMiss()
    {
        // The reported symptom that started this: typing "/skill" did nothing at all.
        SlashCommands.SuggestFor("skill")!.Name.Should().Be(SlashCommands.Skills);
        SlashCommands.SuggestFor("model")!.Name.Should().Be(SlashCommands.Models);
        SlashCommands.SuggestFor("helpme")!.Name.Should().Be(SlashCommands.Help);
        SlashCommands.SuggestFor("zzz").Should().BeNull();
    }

    [Fact]
    public void SlashCommands_FindIsCaseInsensitiveAndRejectsUnknownNames()
    {
        SlashCommands.Find("SKILLS").Should().NotBeNull();
        SlashCommands.Find("nope").Should().BeNull();
    }

    [Fact]
    public void SkillLibrary_ReadsSeveralDirectoriesWithEarlierOnesWinningACollision()
    {
        // The app reads its own container plus the attached project folder, so a repository can
        // carry skills alongside its code - and a host-level skill must be overridable by one.
        var appSkills = Path.Combine(_root, "app-skills");
        var repoSkills = Path.Combine(_root, "repo-skills");
        Directory.CreateDirectory(appSkills);
        Directory.CreateDirectory(repoSkills);
        File.WriteAllText(Path.Combine(appSkills, "review.md"), "app version");
        File.WriteAllText(Path.Combine(repoSkills, "review.md"), "repo version");
        File.WriteAllText(Path.Combine(repoSkills, "deploy.md"), "repo only");

        var skills = new SkillLibrary(appSkills, repoSkills);

        skills.List().Should().BeEquivalentTo(["deploy", "review"]);
        skills.Load("review").Should().Be("app version", "earlier directories win");
        skills.Load("deploy").Should().Be("repo only");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData("back\\slash")]
    [InlineData("")]
    public void SkillLibrary_RefusesToLoadAnythingOutsideItsOwnDirectories(string name)
    {
        var skills = new SkillLibrary(_root);

        skills.Load(name).Should().BeNull();
    }

    [Fact]
    public void SkillLibrary_IgnoresADirectoryThatDoesNotExist()
    {
        // A workspace folder can be detached, or a remembered bookmark can point somewhere gone.
        var skills = new SkillLibrary(Path.Combine(_root, "not-created"), null);

        skills.List().Should().BeEmpty();
        skills.Load("anything").Should().BeNull();
    }

    [Fact]
    public void ModelfileParser_SplitsTheRealSentinelProfileIntoApiCreateFields()
    {
        // The shipped profile's own shape: a comment block, FROM, PARAMETERs, a triple-quoted
        // multi-line SYSTEM, then MESSAGE pairs. /api/create needs each of these as a separate
        // field since the whole-file "modelfile" field was removed.
        var profile = ModelCatalog.SentinelProfile;

        var parsed = OllamaModelfileParser.Parse(profile);

        parsed.From.Should().Be("gemma4");
        parsed.Parameters.Should().Contain(new KeyValuePair<string, string>("temperature", "0.2"));
        parsed.Parameters.Should().Contain(new KeyValuePair<string, string>("num_ctx", "16384"));
        parsed.System.Should().StartWith("You are SentinelGPT");
        parsed.System.Should().Contain("Never invent a source");
        parsed.System.Should().NotContain("\"\"\"", "the fences delimit the block, they aren't part of it");
        parsed.Messages.Should().HaveCount(4);
        parsed.Messages[0].Role.Should().Be("user");
        parsed.Messages[1].Role.Should().Be("assistant");
    }

    [Fact]
    public void ModelfileParser_IgnoresCommentsAndUnknownDirectives()
    {
        var parsed = OllamaModelfileParser.Parse("""
            # a leading comment
            FROM llama3.2
            TEMPLATE {{ .Prompt }}
            PARAMETER temperature 0.7
            SYSTEM "One line only."
            """);

        parsed.From.Should().Be("llama3.2");
        parsed.System.Should().Be("One line only.", "a quoted single-line SYSTEM keeps no quotes");
        parsed.Parameters.Should().ContainSingle();
        parsed.Messages.Should().BeEmpty();
    }

    [Fact]
    public void ModelfileParser_RejectsAProfileWithNoBaseModel()
    {
        var act = () => OllamaModelfileParser.Parse("PARAMETER temperature 0.2");

        act.Should().Throw<InvalidOperationException>().WithMessage("*FROM*");
    }

    [Fact]
    public async Task PullModelAsync_StreamsProgressAndSurfacesAMidStreamFailure()
    {
        // Ollama sends HTTP 200 and only then reports failure inside the body, so a caller that
        // checks the status code alone would report a broken download as a success.
        var body = string.Join('\n',
            """{"status":"pulling manifest"}""",
            """{"status":"pulling 4c27e0f5","completed":50,"total":200}""",
            """{"error":"model 'nope' not found"}""");
        using var client = CreateClient(_ => NdjsonResponse(body));

        var seen = new List<OllamaProgress>();
        var act = async () =>
        {
            await foreach (var progress in client.PullModelAsync("nope", default))
                seen.Add(progress);
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*model 'nope' not found*");
        seen.Should().HaveCount(2);
        seen[0].Fraction.Should().BeNull("a status line with no byte counts must not read as 0%");
        seen[1].Fraction.Should().Be(0.25);
    }

    [Fact]
    public async Task CreateModelAsync_SendsTypedParametersRatherThanStrings()
    {
        // num_ctx has to arrive as a number; sent as "16384" Ollama would not apply it the way
        // the Modelfile means it.
        string? payload = null;
        using var client = CreateClient(request =>
        {
            payload = request.Content!.ReadAsStringAsync().Result;
            return NdjsonResponse("""{"status":"success"}""");
        });
        var profile = OllamaModelfileParser.Parse("""
            FROM gemma4
            PARAMETER num_ctx 16384
            PARAMETER temperature 0.2
            SYSTEM "Be terse."
            MESSAGE user Hello
            """);

        await foreach (var _ in client.CreateModelAsync("sentinelgpt", profile, default)) { }

        payload.Should().Contain("\"num_ctx\":16384").And.NotContain("\"num_ctx\":\"16384\"");
        payload.Should().Contain("\"temperature\":0.2");
        payload.Should().Contain("\"from\":\"gemma4\"");
        payload.Should().Contain("\"system\":\"Be terse.\"");
    }

    [Fact]
    public async Task ListModelDetailsAsync_SurfacesEachModelsCapabilitiesAndSize()
    {
        // Shape copied from a real /api/tags response (Ollama 0.33.3). Capabilities are the
        // whole point: Ollama rejects chat outright for an embedding model and rejects a
        // populated tools array for a model without "tools", so a host that lets a human pick
        // has to filter on these rather than discover them from a failed turn.
        var body = """
            {"models":[
              {"name":"embeddinggemma:latest","details":{"parameter_size":"307.58M"},"capabilities":["embedding"]},
              {"name":"gemma3:12b","details":{"parameter_size":"12.2B"},"capabilities":["completion"]},
              {"name":"gemma4:latest","details":{"parameter_size":"8.0B"},"capabilities":["completion","tools","thinking"]}
            ]}
            """;
        using var client = CreateClient(_ => JsonResponse(body));

        var models = await client.ListModelDetailsAsync(default);

        models.Should().HaveCount(3);
        var embedding = models.Single(model => model.Name == "embeddinggemma:latest");
        embedding.SupportsChat.Should().BeFalse();
        var chatOnly = models.Single(model => model.Name == "gemma3:12b");
        chatOnly.SupportsChat.Should().BeTrue();
        chatOnly.SupportsTools.Should().BeFalse();
        var full = models.Single(model => model.Name == "gemma4:latest");
        full.SupportsChat.Should().BeTrue();
        full.SupportsTools.Should().BeTrue();
        full.SupportsThinking.Should().BeTrue();
        full.ParameterSize.Should().Be("8.0B");
    }

    [Fact]
    public async Task ListModelDetailsAsync_TreatsAModelReportingNoCapabilitiesAsUnusableForChat()
    {
        // Older daemons omit the field entirely. Defaulting to "no capabilities" keeps such a
        // model out of a picker rather than letting it through to fail on first use.
        using var client = CreateClient(_ => JsonResponse("""{"models":[{"name":"mystery:latest"}]}"""));

        var models = await client.ListModelDetailsAsync(default);

        models.Should().ContainSingle();
        models[0].SupportsChat.Should().BeFalse();
        models[0].ParameterSize.Should().BeNull();
    }

    [Fact]
    public async Task ChatStreamAsync_OmitsThinkUnlessTheCallerAsksForIt()
    {
        var payloads = new List<string>();
        using var client = CreateClient(request =>
        {
            payloads.Add(request.Content!.ReadAsStringAsync().Result);
            return NdjsonResponse("""{"message":{"content":"hi"},"done":true}""");
        });

        await foreach (var _ in client.ChatStreamAsync("m", [new OllamaChatMessage("user", "hi")], [], default)) { }
        await foreach (var _ in client.ChatStreamAsync("m", [new OllamaChatMessage("user", "hi")], [], default, think: false)) { }

        payloads[0].Should().NotContain("\"think\"", "a caller with no opinion must leave the model on its own default");
        payloads[1].Should().Contain("\"think\":false");
    }

    [Fact]
    public async Task ChatStreamAsync_AlwaysSendsAToolsArrayEvenWhenEmpty()
    {
        // An empty array is how a model without the "tools" capability stays usable for plain
        // chat - Ollama accepts it and rejects only a populated one - so it must not be dropped.
        var payloads = new List<string>();
        using var client = CreateClient(request =>
        {
            payloads.Add(request.Content!.ReadAsStringAsync().Result);
            return NdjsonResponse("""{"message":{"content":"hi"},"done":true}""");
        });

        await foreach (var _ in client.ChatStreamAsync("m", [new OllamaChatMessage("user", "hi")], [], default)) { }

        payloads[0].Should().Contain("\"tools\":[]");
    }

    [Fact]
    public async Task ToolCallingAgent_ReportsThinkingSeparatelyAndKeepsItOutOfTheAnswer()
    {
        // A reasoning model streams deliberation in a "thinking" field with content still empty.
        // Concatenating the two would show the user the model talking itself through the problem
        // as though it were the answer.
        var body = string.Join('\n',
            """{"message":{"content":"","thinking":"Let me work through this."},"done":false}""",
            """{"message":{"content":"391"},"done":false}""",
            """{"message":{"content":""},"done":true}""");
        using var client = CreateClient(_ => NdjsonResponse(body));
        var agent = new OllamaToolCallingAgent(client, new FakeToolExecutor([]), "deepseek-r1", "system prompt");

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("What is 17 * 23?", default))
            events.Add(e);

        string.Concat(events.Select(e => e.ContentDelta)).Should().Be("391");
        events.Should().ContainSingle(e => e.ThinkingDelta == "Let me work through this.");
        agent.Messages[^1].Content.Should().Be("391");
    }

    [Fact]
    public async Task ToolCallingAgent_WhenAModelOnlyEverThinks_SaysSoInsteadOfReportingAnEmptyResponse()
    {
        // deepseek-r1 answers with content:"" and a populated thinking field even when asked not
        // to think. That used to surface as the generic "Ollama returned an empty response",
        // which pointed at the daemon rather than at the model choice that actually caused it.
        var body = string.Join('\n',
            """{"message":{"content":"","thinking":"Okay, the user just said"},"done":false}""",
            """{"message":{"content":""},"done":true}""");
        using var client = CreateClient(_ => NdjsonResponse(body));
        var agent = new OllamaToolCallingAgent(client, new FakeToolExecutor([]), "deepseek-r1", "system prompt");

        var act = async () =>
        {
            await foreach (var _ in agent.RunTurnStreamingAsync("say hi", default)) { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deepseek-r1*only produced reasoning*");
    }

    [Fact]
    public async Task ToolCallingAgent_WithARepeatedIdenticalToolCall_ExecutesItOnceAndTellsTheModelToStop()
    {
        // The loop this guards against: a model that keeps re-issuing the same call because the
        // result wasn't what it wanted. Rounds 1 and 2 make the identical call; only the first
        // should reach the executor.
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round <= 2
                ? NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"search_wiki","arguments":{"query":"deploy"}}}]},"done":true}""")
                : NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"Nothing in the wiki about that."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""));
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "sentinelgpt", "system prompt", maxRounds: 4);

        var events = new List<OllamaAgentEvent>();
        await foreach (var e in agent.RunTurnStreamingAsync("What does the wiki say about deploys?", default))
            events.Add(e);

        executor.Calls.Should().ContainSingle("the identical second call must be short-circuited, not re-run");
        events.Count(e => e.ToolActivity == "search_wiki").Should().Be(1, "no activity should be reported for a call that never ran");
        agent.Messages.Should().Contain(m =>
            m.Role == "tool" && m.ToolName == "search_wiki" && m.Content.Contains("already made this exact tool call"));
        string.Concat(events.Select(e => e.ContentDelta)).Should().Be("Nothing in the wiki about that.");
    }

    [Fact]
    public async Task ToolCallingAgent_WithTheSameToolButDifferentArguments_RunsBothCalls()
    {
        // The guard must not punish legitimate work - reading two different files, or searching two
        // genuinely different queries, is progress rather than a loop.
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round switch
            {
                1 => NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"read_file","arguments":{"path":"a.cs"}}}]},"done":true}"""),
                2 => NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"read_file","arguments":{"path":"b.cs"}}}]},"done":true}"""),
                _ => NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"Both files look fine."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""))
            };
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("read_file", "Read", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "sentinelgpt", "system prompt", maxRounds: 4);

        await foreach (var _ in agent.RunTurnStreamingAsync("Review a.cs and b.cs", default)) { }

        executor.Calls.Should().HaveCount(2);
        executor.Calls.Select(call => call.ArgumentsJson).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ToolCallingAgent_TreatsReorderedOrReformattedArgumentsAsTheSameCall()
    {
        // Same call, different JSON spelling (property order and whitespace) - a raw string compare
        // would miss this and let the loop through.
        var round = 0;
        using var client = CreateClient(_ =>
        {
            round++;
            return round switch
            {
                1 => NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"search_text","arguments":{"query":"deploy","path":"src"}}}]},"done":true}"""),
                2 => NdjsonResponse("""{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"search_text","arguments":{"path":"src","query":"deploy"}}}]},"done":true}"""),
                _ => NdjsonResponse(string.Join('\n',
                    """{"message":{"content":"No matches."},"done":false}""",
                    """{"message":{"content":""},"done":true}"""))
            };
        });
        var executor = new FakeToolExecutor([new OllamaToolDefinition("search_text", "Search", """{"type":"object"}""")]);
        var agent = new OllamaToolCallingAgent(client, executor, "sentinelgpt", "system prompt", maxRounds: 4);

        await foreach (var _ in agent.RunTurnStreamingAsync("Search for deploy", default)) { }

        executor.Calls.Should().ContainSingle();
    }

    [Fact]
    public void CompositeToolExecutor_OffersEveryUnderlyingExecutorsDefinitions()
    {
        var wiki = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var files = new FakeToolExecutor([
            new OllamaToolDefinition("read_file", "Read", """{"type":"object"}"""),
            new OllamaToolDefinition("write_file", "Write", """{"type":"object"}""")
        ]);

        var composite = new CompositeToolExecutor([wiki, files]);

        composite.Definitions.Select(definition => definition.Name)
            .Should().BeEquivalentTo(["search_wiki", "read_file", "write_file"]);
    }

    [Fact]
    public async Task CompositeToolExecutor_RoutesEachCallToTheExecutorThatDeclaresIt()
    {
        var wiki = new FakeToolExecutor([new OllamaToolDefinition("search_wiki", "Search", """{"type":"object"}""")]);
        var files = new FakeToolExecutor([new OllamaToolDefinition("read_file", "Read", """{"type":"object"}""")]);
        var composite = new CompositeToolExecutor([wiki, files]);

        await composite.ExecuteAsync(new OllamaToolCall("read_file", """{"path":"a.cs"}"""), default);

        files.Calls.Should().ContainSingle(call => call.Name == "read_file");
        wiki.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CompositeToolExecutor_ReturnsAnErrorWhenNoExecutorDeclaresTheTool()
    {
        // Reachable in the real app: the wiki tools disappear from Definitions the moment the
        // grounding key is removed, so an in-flight call can name a tool nothing owns any more.
        var composite = new CompositeToolExecutor([new FakeToolExecutor([])]);

        var result = await composite.ExecuteAsync(new OllamaToolCall("search_wiki", "{}"), default);

        result.Should().Contain("Unknown tool: search_wiki");
    }

    [Theory]
    [InlineData("""{"name":"search_wiki","parameters":{"query":"x"}}""", true)]
    [InlineData("""{"name":"read_file","arguments":{"path":"x","bad":undefined}}""", true)]
    [InlineData("""The deploy process pushes to main and the pipeline handles it.""", false)]
    [InlineData("""{"ok":true}""", false)]
    [InlineData("", false)]
    // A model unsure enough to narrate around a call instead of just issuing it - "I don't have
    // direct access, so I'll use get_page: {json}" - used to slip past this check entirely
    // because the content didn't *start* with '{'. It's just as much a failed attempt as a bare
    // malformed object and needs the same corrective retry, not a pass-through as a real answer.
    [InlineData("""I don't have direct access, so I will use the "get_page" function: {"name":"get_page","parameters":{"pageId":"<your_page_id>"}}""", true)]
    // An unrelated JSON example in a genuine answer (no tool-call shape: no "arguments"/
    // "parameters" alongside "name") must not false-positive into an unnecessary retry.
    [InlineData("""Sure, here's an example config: {"name":"my-app","version":"1.0"} - nothing to do with tools.""", false)]
    public void LooksLikeFailedToolCallAttempt_RecognizesJsonShapedAttemptsOnly(string content, bool expected) =>
        OllamaToolCallParsing.LooksLikeFailedToolCallAttempt(content).Should().Be(expected);

    [Fact]
    public async Task ConversationSessionStore_RoundTripsMessagesIncludingToolCalls()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var messages = new List<OllamaChatMessage>
        {
            new("system", "s"),
            new("user", "Read a.cs"),
            new("tool", "{}") { ToolName = "read_file" }
        };

        var path = await store.SaveAsync(null, "llama3.2", messages, default);
        var loaded = await store.LoadAsync(path, default);

        loaded.Should().NotBeNull();
        loaded!.Model.Should().Be("llama3.2");
        loaded.Messages.Should().HaveCount(3);
        loaded.Messages[2].ToolName.Should().Be("read_file");
        store.List().Should().ContainSingle(item => item.Path == path);
    }

    [Fact]
    public async Task ConversationSessionStore_SaveWithNoExistingPathAlwaysMintsANewFile()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var messages = new[] { new OllamaChatMessage("system", "s"), new OllamaChatMessage("user", "u") };

        var first = await store.SaveAsync(null, "llama3.2", messages, default);
        var second = await store.SaveAsync(null, "llama3.2", messages, default);

        first.Should().NotBe(second);
        store.List().Should().HaveCount(2);
    }

    [Fact]
    public async Task ConversationSessionStore_DeleteRemovesTheFile()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var path = await store.SaveAsync(null, "llama3.2", [new OllamaChatMessage("system", "s")], default);

        store.Delete(path);

        File.Exists(path).Should().BeFalse();
        store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task ConversationSessionStore_RoundTripsAWorkspaceScopedConversationsWorkspaceRoot()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var workspace = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(workspace);

        var path = await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("system", "s")], default, workspaceRoot: workspace);
        var loaded = await store.LoadAsync(path, default);

        loaded.Should().NotBeNull();
        loaded!.WorkspaceRoot.Should().Be(Path.GetFullPath(workspace));
    }

    [Fact]
    public async Task ConversationSessionStore_List_ExcludesWorkspaceScopedConversations()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var workspace = Path.Combine(_root, "repo-a");
        Directory.CreateDirectory(workspace);
        await store.SaveAsync(null, "llama3.2", [new OllamaChatMessage("user", "ordinary chat")], default);
        await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("user", "dev chat")], default, workspaceRoot: workspace);

        var ordinary = store.List();

        ordinary.Should().ContainSingle();
        ordinary[0].Conversation.Messages[0].Content.Should().Be("ordinary chat");
    }

    [Fact]
    public async Task ConversationSessionStore_ListForWorkspace_ReturnsOnlyThatWorkspacesConversationsAndNotOrdinaryChats()
    {
        var store = new ConversationSessionStore(Path.Combine(_root, "sessions"));
        var workspaceA = Path.Combine(_root, "repo-a");
        var workspaceB = Path.Combine(_root, "repo-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        await store.SaveAsync(null, "llama3.2", [new OllamaChatMessage("user", "ordinary chat")], default);
        var pathA = await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("user", "repo a chat")], default, workspaceRoot: workspaceA);
        await store.SaveAsync(
            null, "qwen2.5-coder", [new OllamaChatMessage("user", "repo b chat")], default, workspaceRoot: workspaceB);

        var forWorkspaceA = store.ListForWorkspace(workspaceA);

        forWorkspaceA.Should().ContainSingle(item => item.Path == pathA);
        forWorkspaceA[0].Conversation.Messages[0].Content.Should().Be("repo a chat");
    }

    [Fact]
    public async Task ApprovedMemoryStore_SurfacesAPreviouslyApprovedAnswerForARelatedQuestion()
    {
        var store = new ApprovedMemoryStore(Path.Combine(_root, "approved-memory.json"));
        await store.AppendAsync("How do I deploy the affiliate service?", "Push to main; the pipeline handles it.", default);

        var context = await store.BuildContextAsync("What's the deploy process for affiliate?", default);
        var unrelated = await store.BuildContextAsync("What's my favorite color?", default);

        context.Should().Contain("Push to main; the pipeline handles it.");
        unrelated.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovedMemoryStore_ShouldNotMatchSolelyOnTheWordYou()
    {
        // Regression test: "you" was missing from StopWords (only "your" was listed), so any
        // question containing "you" - true of nearly all natural phrasing - term-overlap-matched
        // any prior approved exchange whose own question/answer also happened to contain "you",
        // regardless of actual topic. Confirmed live 2026-08-27 in the native Mac app: "Are you
        // faster now?" pulled in a completely unrelated prior "Can you find my ... page?" answer.
        var store = new ApprovedMemoryStore(Path.Combine(_root, "approved-memory2.json"));
        await store.AppendAsync("Can you find my Q3 sales report?", "Q3 sales totaled $42,000 across all regions.", default);

        var unrelated = await store.BuildContextAsync("Are you feeling faster today?", default);

        unrelated.Should().BeEmpty();
    }

    private static OllamaClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHttpMessageHandler(request => Task.FromResult(respond(request)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        return new OllamaClient(http.BaseAddress!, http);
    }

    private static HttpResponseMessage NdjsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeToolExecutor(IReadOnlyList<OllamaToolDefinition> definitions) : IOllamaToolExecutor
    {
        public List<OllamaToolCall> Calls { get; } = [];
        public IReadOnlyList<OllamaToolDefinition> Definitions => definitions;

        public Task<string> ExecuteAsync(OllamaToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return Task.FromResult("""{"result":"ok"}""");
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
