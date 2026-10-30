using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Infrastructure.Services.SentinelAgent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// SentinelGPT as an agent in the app (30-10-26): the loop, what the chat remembers, roles,
// Confirm, Undo, and each area's tools against a real SQLite database. The model is scripted.
public sealed class SentinelAgentTests
{
    private static readonly SentinelAgentUser Admin = new("grant", [AppRoles.Admin]);
    private static readonly SentinelAgentUser Author = new("writer", [AppRoles.Author]);

    [Fact]
    public async Task Agent_ShouldMakeAToDoList_ThenAddToIt_ThreeTurnsLater_FromWhatTheChatMade()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();

        var first = await harness.TurnAsync(chat, Admin, "Make a grocery list with milk, eggs and bread",
            Call("create_todo_list", new { title = "Groceries", items = new[] { "milk", "eggs", "bread" } }),
            Say("Made your grocery list."));
        first.Actions.Should().ContainSingle().Which.Summary.Should().Be("Created to-do list 'Groceries' with 3 items");
        first.Actions[0].Link.Should().StartWith("/admin/sentinel?page=");
        first.Actions[0].CanUndo.Should().BeTrue();

        await harness.TurnAsync(chat, Admin, "What's 2 + 2?", Say("4"));
        await harness.TurnAsync(chat, Admin, "Thanks", Say("Any time."));

        // "the previous list" is resolved from what the chat made, listed in the system prompt.
        var model = new ScriptedModel(
            messages =>
            {
                messages.Count(message => message.Role == "user").Should().Be(4, "the three earlier turns come back as history");
                var id = Regex.Match(messages[0].Content, @"sentinel-page id ([0-9a-f-]{36})").Groups[1].Value;
                id.Should().NotBeEmpty("the list's id is in 'What this chat has changed'");
                return Call("add_todo_items", new { pageId = id, items = new[] { "butter" } });
            },
            _ => Say("Added butter."));
        var last = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "Oh, add butter to that list", null, model, "test"), _ => Task.CompletedTask, default);

        last.Actions.Should().ContainSingle().Which.Summary.Should().Be("Added 1 item to 'Groceries'");
        (await harness.ToDosAsync("Groceries")).Should().Equal("milk", "eggs", "bread", "butter");
    }

    [Fact]
    public async Task Agent_ShouldOnlyOfferTheToolsTheUsersRoleAllows()
    {
        await using var harness = await Harness.CreateAsync();
        var asAuthor = new ScriptedModel(_ => Say("ok"));
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Author, "hi", null, asAuthor, "test"), _ => Task.CompletedTask, default);
        var asAdmin = new ScriptedModel(_ => Say("ok"));
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "hi", null, asAdmin, "test"), _ => Task.CompletedTask, default);

        var authorTools = asAuthor.Tools[0].Select(tool => tool.Name).ToList();
        authorTools.Should().Contain(["create_todo_list", "find_sentinel_pages", SentinelAgentRunner.UndoToolName])
            .And.NotContain(["add_card", "list_boards", "create_site_page_draft", "search_app"], "boards and app-wide search are Admin pages; site pages are Admin/Contributor");
        asAdmin.Tools[0].Select(tool => tool.Name).Should().Contain(["add_card", "create_site_page_draft", "search_app"]);
    }

    [Fact]
    public async Task Confirm_ShouldWaitForThePerson_RunExactlyWhatWasShown_AndUndoBringsItBack()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var made = await harness.TurnAsync(chat, Admin, "Make a page called Old notes",
            Call("create_sentinel_page", new { title = "Old notes", content = "Some notes" }), Say("Done."));
        var pageId = made.Actions[0].Link!.Split('=')[1];

        var proposed = await harness.TurnAsync(chat, Admin, "Delete it",
            Call("trash_sentinel_page", new { pageId }), Say("It's waiting for your Confirm."));
        var proposal = proposed.Actions.Should().ContainSingle().Subject;
        proposal.AwaitingConfirm.Should().BeTrue();
        proposal.Summary.Should().Be("Move the Sentinel page 'Old notes' to the trash");
        (await harness.PageAsync(pageId)).TrashedAt.Should().BeNull("nothing happens before the Confirm");

        var confirmed = await harness.Runner.ConfirmAsync(proposal.Id, Admin, approve: true, default);
        confirmed.Status.Should().Be(SentinelAgentActionStatuses.Done);
        (await harness.PageAsync(pageId)).TrashedAt.Should().NotBeNull();
        var act = () => harness.Runner.ConfirmAsync(proposal.Id, Admin, approve: true, default);
        await act.Should().ThrowAsync<InvalidOperationException>("a proposal runs once");

        (await harness.Runner.UndoAsync(confirmed.Id, Admin, default)).Status.Should().Be(SentinelAgentActionStatuses.Undone);
        (await harness.PageAsync(pageId)).TrashedAt.Should().BeNull();

        var someoneElse = () => harness.Runner.UndoAsync(confirmed.Id, new SentinelAgentUser("other", [AppRoles.Admin]), default);
        await someoneElse.Should().ThrowAsync<InvalidOperationException>("only the person whose chat it was can act on it");
    }

    [Fact]
    public async Task Decline_ShouldLeaveThingsAlone()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var made = await harness.TurnAsync(chat, Admin, "Make a list", Call("create_todo_list", new { title = "Keep" }), Say("Done."));
        var pageId = made.Actions[0].Link!.Split('=')[1];
        var proposed = await harness.TurnAsync(chat, Admin, "Delete it", Call("trash_sentinel_page", new { pageId }), Say("Waiting."));

        (await harness.Runner.ConfirmAsync(proposed.Actions[0].Id, Admin, approve: false, default)).Status.Should().Be(SentinelAgentActionStatuses.Declined);
        (await harness.PageAsync(pageId)).TrashedAt.Should().BeNull();
    }

    [Fact]
    public async Task Undo_ShouldPutAListBack_ButNotOverSomeoneElsesLaterEdit()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var made = await harness.TurnAsync(chat, Admin, "List", Call("create_todo_list", new { title = "Chores", items = "- dishes\n- laundry" }), Say("Done."));
        var pageId = made.Actions[0].Link!.Split('=')[1];
        (await harness.ToDosAsync("Chores")).Should().Equal(["dishes", "laundry"], "a list sent as lines of text still becomes items");

        var ticked = await harness.TurnAsync(chat, Admin, "Done the dishes", Call("set_todo_done", new { pageId, item = "dish" }), Say("Ticked."));
        ticked.Actions[0].Summary.Should().Be("Ticked 'dishes' on 'Chores'");
        (await harness.Runner.UndoAsync(ticked.Actions[0].Id, Admin, default)).Status.Should().Be(SentinelAgentActionStatuses.Undone);

        var added = await harness.TurnAsync(chat, Admin, "Add hoovering", Call("add_todo_items", new { pageId, items = new[] { "hoovering" } }), Say("Added."));
        await harness.EditByHandAsync(pageId);
        var undo = () => harness.Runner.UndoAsync(added.Actions[0].Id, Admin, default);
        (await undo.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("has changed since");
    }

    [Fact]
    public async Task Agent_ShouldNotMakeTheSameThingTwice_WhenTheModelRepeatsACall()
    {
        await using var harness = await Harness.CreateAsync();
        var call = new OllamaToolCall("create_todo_list", JsonSerializer.Serialize(new { title = "Once" }));
        var turn = await harness.TurnAsync(Guid.NewGuid(), Admin, "Make a list",
            new OllamaChatResponse(string.Empty, [call, call]), Say("Done."));

        turn.Actions.Should().ContainSingle();
        (await harness.PagesTitledAsync("Once")).Should().Be(1);
    }

    [Fact]
    public async Task Boards_ShouldAddAndMoveACard_AndUndoTheMove()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var created = await harness.TurnAsync(chat, Admin, "Make a launch board", Call("create_board", new { title = "Launch" }), Say("Done."));
        var boardId = created.Actions[0].Link!.Split('/').Last();

        var model = new ScriptedModel(
            _ => Call("add_card", new { boardId, title = "Write the post", column = "to do" }),
            messages => Call("move_card", new { boardId, cardId = LastResult(messages)["cardId"]!.GetValue<string>(), column = "Done" }),
            _ => Say("Added and finished."));
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "Add a card and mark it done", null, model, "test"), _ => Task.CompletedTask, default);

        turn.Actions.Select(action => action.Summary).Should().Equal("Added 'Write the post' to To do on 'Launch'", "Moved 'Write the post' from To do to Done");
        (await harness.ColumnOfAsync(boardId, "Write the post")).Should().Be("Done");
        await harness.Runner.UndoAsync(turn.Actions[1].Id, Admin, default);
        (await harness.ColumnOfAsync(boardId, "Write the post")).Should().Be("To do");
    }

    [Fact]
    public async Task SitePages_ShouldBeDraftedPrivately_AndPublishedOnlyAfterAConfirm()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SiteAsync();
        var chat = Guid.NewGuid();
        var drafted = await harness.TurnAsync(chat, Admin, "Draft a services page",
            Call("create_site_page_draft", new
            {
                title = "Services",
                sections = new object[] { new { heading = "What we do", text = "Websites and **support**." }, new { template = "cta-banner" } }
            }),
            Say("Drafted."));
        drafted.Actions[0].Summary.Should().Be("Drafted the site page 'Services' (2 sections)");
        var pageId = Guid.Parse(drafted.Actions[0].Link!.Split('/')[3]);
        var page = await harness.SitePageAsync(pageId);
        page.Status.Should().Be(CmsPageStatuses.Draft);
        var layout = CmsBuilderJson.ParseLayoutOrEmpty(page.BlocksJson);
        layout.Sections[0].Columns[0].Widgets.Select(widget => (widget.WidgetType, widget.Props["text"]))
            .Should().Equal(("heading", "What we do"), ("paragraph", "Websites and **support**."));

        var proposed = await harness.TurnAsync(chat, Admin, "Publish it", Call("publish_site_page", new { pageId }), Say("Waiting for your Confirm."));
        (await harness.SitePageAsync(pageId)).Status.Should().Be(CmsPageStatuses.Draft);
        var published = await harness.Runner.ConfirmAsync(proposed.Actions[0].Id, Admin, approve: true, default);
        (await harness.SitePageAsync(pageId)).Status.Should().Be(CmsPageStatuses.Published);

        await harness.Runner.UndoAsync(published.Id, Admin, default);
        (await harness.SitePageAsync(pageId)).Status.Should().Be(CmsPageStatuses.Draft, "undoing a publish takes it back off the site");
    }

    [Fact]
    public async Task ToolErrors_ShouldGoBackToTheModel_SoItCanPutThemRight()
    {
        await using var harness = await Harness.CreateAsync();
        var model = new ScriptedModel(
            _ => Call("add_todo_items", new { pageId = "groceries", items = new[] { "milk" } }),
            messages =>
            {
                LastResult(messages)["error"]!.GetValue<string>().Should().Contain("isn't an id");
                return Say("I couldn't find that list.");
            });
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Add milk", null, model, "test"), _ => Task.CompletedTask, default);
        turn.Answer.Should().Be("I couldn't find that list.");
        turn.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task BigJobs_ShouldShowAPlanFirst_AndOnlyStartWhenThePersonPressesGo()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        // One scripted reply only: the turn must stop at the plan, not ask the model again.
        var planned = await harness.TurnAsync(chat, Admin, "Set up the launch: a board, two cards and a checklist",
            Call(SentinelAgentRunner.PlanToolName, new { steps = new[] { "Create the Launch board", "Add two cards", "Make a launch checklist" } }));

        var plan = planned.Actions.Should().ContainSingle().Subject;
        plan.AwaitingConfirm.Should().BeTrue();
        plan.Summary.Should().Be("1. Create the Launch board\n2. Add two cards\n3. Make a launch checklist");
        planned.Answer.Should().Contain("Press Go");
        (await harness.PagesTitledAsync("Launch checklist")).Should().Be(0, "nothing happens before Go");

        (await harness.Runner.ConfirmAsync(plan.Id, Admin, approve: true, default)).Status.Should().Be(SentinelAgentActionStatuses.Done);
        var model = new ScriptedModel(
            messages =>
            {
                messages[0].Content.Should().Contain("[approved, Go pressed] Plan: 1. Create the Launch board; 2. Add two cards");
                return Call("create_todo_list", new { title = "Launch checklist" });
            },
            _ => Say("Started."));
        await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, SentinelAgentRunner.GoMessage, null, model, "test"), _ => Task.CompletedTask, default);
        (await harness.PagesTitledAsync("Launch checklist")).Should().Be(1);
    }

    [Fact]
    public async Task BigJobs_ThatStartWithoutAPlan_ShouldBeRolledBack_AndPlannedWhole()
    {
        await using var harness = await Harness.CreateAsync();
        var events = new List<SentinelAgentEvent>();
        var model = new ScriptedModel(
            _ => Call("create_todo_list", new { title = "A" }),
            _ => Call("create_todo_list", new { title = "B" }),
            _ => Call("create_todo_list", new { title = "C" }),
            _ => new OllamaChatResponse(string.Empty, [
                new OllamaToolCall("create_todo_list", JsonSerializer.Serialize(new { title = "D" })),
                new OllamaToolCall("create_todo_list", JsonSerializer.Serialize(new { title = "E" }))]),
            messages =>
            {
                var results = messages.Where(message => message.Role == "tool").TakeLast(2).Select(message => message.Content).ToList();
                results[0].Should().Contain("needs a plan first").And.Contain("have been undone");
                results[1].Should().Contain("call show_plan", "nothing more changes in the turn until the plan is shown");
                return Call(SentinelAgentRunner.PlanToolName, new { steps = new[] { "A", "B", "C", "D", "E" } });
            });
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Make five lists", null, model, "test"),
            agentEvent => { events.Add(agentEvent); return Task.CompletedTask; }, default);

        turn.Actions.Should().ContainSingle().Which.Tool.Should().Be(SentinelAgentRunner.PlanToolName);
        events.OfType<SentinelAgentWithdrawn>().Should().HaveCount(3);
        foreach (var title in new[] { "A", "B", "C" }) (await harness.TrashedAsync(title)).Should().BeTrue($"'{title}' was rolled back");
        (await harness.PagesTitledAsync("D")).Should().Be(0);
        (await harness.PagesTitledAsync("E")).Should().Be(0);
    }

    [Fact]
    public async Task Memories_ShouldReachEveryLaterChat_AndBeForgettable()
    {
        await using var harness = await Harness.CreateAsync();
        var remembered = await harness.TurnAsync(Guid.NewGuid(), Admin, "Remember that we host on DigitalOcean",
            Call("remember", new { fact = "We host on DigitalOcean" }), Say("Got it."));
        remembered.Actions[0].Summary.Should().Be("Remembered: We host on DigitalOcean");

        var model = new ScriptedModel(messages =>
        {
            messages[0].Content.Should().Contain("What you've been asked to remember").And.Contain("- We host on DigitalOcean (memory id");
            return Say("DigitalOcean.");
        });
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Where do we host?", null, model, "test"), _ => Task.CompletedTask, default);

        var others = new ScriptedModel(messages =>
        {
            messages[0].Content.Should().NotContain("DigitalOcean", "memories are each person's own");
            return Say("I don't know.");
        });
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Author, "Where do we host?", null, others, "test"), _ => Task.CompletedTask, default);

        await harness.Runner.UndoAsync(remembered.Actions[0].Id, Admin, default);
        (await harness.Runner.ListMemoriesAsync(Admin, default)).Should().BeEmpty();

        var added = await harness.Runner.RememberAsync(Admin, "Boards' last column is Shipped", default);
        (await harness.Runner.ListMemoriesAsync(Admin, default)).Select(memory => memory.Fact).Should().Equal("Boards' last column is Shipped");
        await harness.Runner.ForgetAsync(Admin, added.Id, default);
        (await harness.Runner.ListMemoriesAsync(Admin, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Activity_ShouldListYourChangesNewestFirst_AndEveryonesOnlyForAnAdmin()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.TurnAsync(Guid.NewGuid(), Admin, "a", Call("create_todo_list", new { title = "First" }), Say("ok"));
        await harness.TurnAsync(Guid.NewGuid(), Admin, "b", Call("create_todo_list", new { title = "Second" }), Say("ok"));
        await harness.TurnAsync(Guid.NewGuid(), Author, "c", Call("create_todo_list", new { title = "Theirs" }), Say("ok"));

        var mine = await harness.Runner.ListActivityAsync(Admin, everyone: false, default);
        mine.Select(row => row.Action.Summary).Should().Equal("Created to-do list 'Second'", "Created to-do list 'First'");
        mine.Should().OnlyContain(row => row.Mine && row.Action.CanUndo);

        var all = await harness.Runner.ListActivityAsync(Admin, everyone: true, default);
        all.Should().HaveCount(3);
        all.Single(row => row.Who == "writer").Action.CanUndo.Should().BeFalse("an Admin sees others' changes but only undoes their own");
        (await harness.Runner.ListActivityAsync(Author, everyone: true, default)).Should().ContainSingle("only an Admin sees everyone's");
    }

    [Fact]
    public async Task ThisTicket_ShouldBecomeALinkedCard_FromTheOpenRecordsId()
    {
        await using var harness = await Harness.CreateAsync();
        var ticketId = await harness.TicketAsync("Invoice PDF won't open");
        var page = GwsBusinessSuite.Application.Assistant.AssistantPrompt.DescribeContext(
            new GwsBusinessSuite.Application.Assistant.AssistantPageContext("Support", "Ticket", "Invoice PDF won't open", RecordId: ticketId.ToString()), null);
        page.Should().Contain($"Open record: Ticket \"Invoice PDF won't open\" (id {ticketId})");

        var created = await harness.TurnAsync(Guid.NewGuid(), Admin, "Make a support board", Call("create_board", new { title = "Support" }), Say("ok"));
        var boardId = created.Actions[0].Link!.Split('/').Last();
        var model = new ScriptedModel(
            messages =>
            {
                var id = Regex.Match(messages[0].Content, @"Ticket ""[^""]+"" \(id ([0-9a-f-]{36})\)").Groups[1].Value;
                return Call("add_ticket_card", new { boardId, ticketId = id, column = "To do" });
            },
            _ => Say("Added."));
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Add this ticket to the support board", page, model, "test"), _ => Task.CompletedTask, default);

        turn.Actions.Should().ContainSingle().Which.Summary.Should().Be("Added the ticket 'Invoice PDF won't open' to To do on 'Support'");
        (await harness.ColumnOfAsync(boardId, "Invoice PDF won't open")).Should().Be("To do");
    }

    [Fact]
    public async Task Relay_ShouldLetATurnThinkOnThePersonsMac_WhileTheMacKeepsAsking()
    {
        await using var harness = await Harness.CreateAsync();
        var clock = new ManualClock();
        var relay = new SentinelAgentRelay(clock);
        relay.ModelsFor("grant").Should().BeEmpty("no Mac has asked yet");

        // Grant's Mac: answers each call as Ollama would, a tool call and then the answer.
        var replies = new Queue<string>([ToolCallReply("create_todo_list", new { title = "From the Mac" }), TextReply("Made it on your Mac.")]);
        using var closeApp = new CancellationTokenSource();
        var mac = Task.Run(async () =>
        {
            while (true)
            {
                if (await relay.TakeAsync("grant", ["embeddinggemma:latest", "sentinelgpt:latest"], closeApp.Token) is not { } call) continue;
                JsonNode.Parse(call.PayloadJson)!["model"]!.GetValue<string>().Should().Be("sentinelgpt:latest");
                relay.Complete("grant", call.Id, replies.Dequeue(), null).Should().BeTrue();
            }
        });
        for (var i = 0; i < 200 && relay.ModelsFor("grant").Count == 0; i++) await Task.Delay(10);
        relay.ModelsFor("grant").Should().Equal(["sentinelgpt:latest"], "only models that can use tools");
        relay.ModelsFor("writer").Should().BeEmpty("a Mac only serves the person it's signed in as");

        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Make a list", null,
            new RelayedOllamaService(relay, "grant"), "sentinelgpt:latest"), _ => Task.CompletedTask, default);
        turn.Answer.Should().Be("Made it on your Mac.");
        (await harness.PagesTitledAsync("From the Mac")).Should().Be(1, "the tool ran on the server");

        closeApp.Cancel();
        await mac.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        clock.Advance(SentinelAgentRelay.OnlineFor + TimeSpan.FromSeconds(1));
        relay.ModelsFor("grant").Should().BeEmpty("a Mac that stopped asking is gone");
    }

    [Fact]
    public async Task Relay_ShouldNotHandACallToSomeoneElsesMac_AndGiveUpOnASilentOne()
    {
        var relay = new SentinelAgentRelay(TimeProvider.System, callTimeout: TimeSpan.FromMilliseconds(400));
        var waiting = relay.ChatAsync("grant", "{}", default);
        using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var otherMac = () => relay.TakeAsync("writer", ["llama3.2:latest"], brief.Token);
        await otherMac.Should().ThrowAsync<OperationCanceledException>("writer's Mac never gets grant's call");

        var silent = () => waiting;
        (await silent.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("didn't answer in time");
        relay.Complete("grant", Guid.NewGuid(), "{}", null).Should().BeFalse("nothing waits for an unknown call");
    }

    [Fact]
    public async Task MacTabTurns_ShouldRunOnTheServer_ThinkOnTheMac_AndStayThePersonsOwn()
    {
        await using var harness = await Harness.CreateAsync();
        var relay = new SentinelAgentRelay(TimeProvider.System);
        var turns = new SentinelAgentTurns(harness.Runner, relay, TimeProvider.System, NullLogger<SentinelAgentTurns>.Instance);
        turns.Start(Admin, Guid.NewGuid(), "Make a list", null).Problem.Should().Contain("isn't connected", "no Mac is asking yet");

        var replies = new Queue<string>([ToolCallReply("create_todo_list", new { title = "From the tab" }), TextReply("Done from the tab.")]);
        using var closeApp = new CancellationTokenSource();
        var mac = Task.Run(async () =>
        {
            while (true)
            {
                if (await relay.TakeAsync("grant", ["llama3.2:latest", "sentinelgpt:latest"], closeApp.Token) is { } call)
                {
                    JsonNode.Parse(call.PayloadJson)!["model"]!.GetValue<string>().Should().Be("llama3.2:latest", "the tab's chosen model goes first");
                    relay.Complete("grant", call.Id, replies.Dequeue(), null);
                }
            }
        });
        for (var i = 0; i < 200 && relay.ModelsFor("grant").Count == 0; i++) await Task.Delay(10);

        var (id, problem) = turns.Start(Admin, Guid.NewGuid(), "Make a list", "llama3.2:latest");
        problem.Should().BeNull();
        SentinelAgentTurns.Snapshot? snapshot = null;
        for (var i = 0; i < 300 && snapshot is not { Status: not SentinelAgentTurns.Statuses.Running }; i++)
        {
            await Task.Delay(20);
            snapshot = turns.Get(id!.Value, "grant");
        }

        snapshot!.Status.Should().Be(SentinelAgentTurns.Statuses.Done);
        snapshot.Answer.Should().Be("Done from the tab.");
        snapshot.Steps.Should().Equal("Creating to-do list 'From the tab'");
        snapshot.Actions.Should().ContainSingle().Which.CanUndo.Should().BeTrue();
        turns.Get(id!.Value, "writer").Should().BeNull("a turn is only its starter's");
        turns.Stop(id!.Value, "writer").Should().BeFalse();

        // What the Mac app reads (AgentTurnSnapshot / AgentActionItem in NativeSessionClient).
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var field in new[] { "\"status\"", "\"steps\"", "\"answer\"", "\"model\"", "\"goMessage\"", "\"id\"", "\"tool\"", "\"summary\"", "\"link\"", "\"canUndo\"", "\"awaitingConfirm\"" })
            json.Should().Contain(field);

        closeApp.Cancel();
        await mac.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    private static string ToolCallReply(string name, object arguments) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments } } } } });

    private static string TextReply(string text) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = text } });

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void LocalCandidates_ShouldPutToolCapableModelsFirst_AndLeaveOutTheRest()
    {
        string[] installed = ["gemma4:latest", "sentinelgpt:latest", "embeddinggemma:latest", "deepseek-r1:latest", "qwen2.5-coder:latest",
            "llama3.2:latest", "qwen3.6:latest", "deepseek-r1:14b", "gemma3:12b", "qwen3:14b", "x/z-image-turbo:latest"];

        SentinelAgentModels.LocalCandidates(installed).Should().Equal(
            "sentinelgpt:latest", "gemma4:latest", "qwen3.6:latest", "qwen3:14b", "qwen2.5-coder:latest", "llama3.2:latest");
    }

    [Fact]
    public void ChatReply_ShouldReadToolCalls_WhetherArgumentsAreAnObjectOrAString()
    {
        var reply = OllamaChatPayload.Parse("""
            {"message":{"role":"assistant","content":"","tool_calls":[
              {"function":{"name":"a","arguments":{"title":"x"}}},
              {"function":{"name":"b","arguments":"{\"title\":\"y\"}"}}]}}
            """);

        reply.ToolCalls.Select(call => (call.Name, JsonNode.Parse(call.ArgumentsJson)!["title"]!.GetValue<string>())).Should().Equal(("a", "x"), ("b", "y"));
    }

    private static JsonObject LastResult(IReadOnlyList<OllamaChatMessage> messages) =>
        JsonNode.Parse(messages.Last(message => message.Role == "tool").Content)!.AsObject();

    private static OllamaChatResponse Call(string name, object arguments) =>
        new(string.Empty, [new OllamaToolCall(name, JsonSerializer.Serialize(arguments))]);

    private static OllamaChatResponse Say(string text) => new(text, []);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private Harness(SqliteConnection connection, ServiceProvider services)
        {
            _connection = connection;
            _services = services;
        }

        public SentinelAgentRunner Runner => _services.GetRequiredService<SentinelAgentRunner>();

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();

            var services = new ServiceCollection()
                .AddScoped(_ => new ApplicationDbContext(options))
                .AddScoped<IAppDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>())
                .AddScoped<IWikiService>(provider => new WikiService(provider.GetRequiredService<IAppDbContext>()))
                .AddScoped<IWikiDatabaseService>(provider => new WikiDatabaseService(provider.GetRequiredService<IAppDbContext>()))
                .AddScoped<IKanbanBoardService>(provider => new KanbanBoardService(provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<IWikiDatabaseService>()))
                .AddScoped<ICmsBuilderService>(provider => new CmsBuilderService(provider.GetRequiredService<IAppDbContext>()))
                .AddScoped<ISuiteSearchService, NoSuiteSearch>()
                .AddScoped<ISentinelAgentToolset, SentinelPageAgentTools>()
                .AddScoped<ISentinelAgentToolset, BoardAgentTools>()
                .AddScoped<ISentinelAgentToolset, SitePageAgentTools>()
                .AddScoped<ISentinelAgentToolset>(provider => new ResearchAgentTools(provider.GetRequiredService<ISuiteSearchService>()))
                .AddScoped<ISentinelAgentToolset, MemoryAgentTools>()
                .AddSingleton(TimeProvider.System)
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<SentinelAgentRunner>()
                .BuildServiceProvider();
            return new Harness(connection, services);
        }

        // A turn whose model replies are fixed in advance, one per round.
        public Task<SentinelAgentTurn> TurnAsync(Guid chat, SentinelAgentUser user, string message, params OllamaChatResponse[] replies) =>
            Runner.RunAsync(new SentinelAgentRequest(chat, user, message, null,
                new ScriptedModel(replies.Select(reply => (Func<IReadOnlyList<OllamaChatMessage>, OllamaChatResponse>)(_ => reply)).ToArray()), "test"),
                _ => Task.CompletedTask, default);

        public async Task<IReadOnlyList<string>> ToDosAsync(string title)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var page = await db.WikiPages.AsNoTracking().SingleAsync(item => item.Title == title);
            return WikiBlockJson.ParseBlocks(page.BlocksJson).Where(block => block.Type == WikiBlockTypes.ToDo).Select(block => block.PlainText).ToList();
        }

        public async Task NoteAsync(string title, string text)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWikiService>().SavePageAsync(
                new WikiPageEditorModel { Title = title, BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(text)) }, "grant");
        }

        public async Task<bool> TrashedAsync(string title)
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IAppDbContext>().WikiPages.AsNoTracking().SingleAsync(item => item.Title == title)).TrashedAt is not null;
        }

        public async Task<int> PagesTitledAsync(string title)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().WikiPages.CountAsync(item => item.Title == title);
        }

        public async Task<WikiPage> PageAsync(string id)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().WikiPages.AsNoTracking().SingleAsync(item => item.Id == Guid.Parse(id));
        }

        // Someone edits the list in Sentinel after the agent did.
        public async Task EditByHandAsync(string id)
        {
            await using var scope = _services.CreateAsyncScope();
            var wiki = scope.ServiceProvider.GetRequiredService<IWikiService>();
            var page = (await wiki.GetPageAsync(Guid.Parse(id)))!;
            await wiki.SavePageAsync(new WikiPageEditorModel
            {
                WikiPageId = page.Id,
                Title = page.Title,
                Slug = page.Slug,
                ParentWikiPageId = page.ParentWikiPageId,
                ExpectedContentVersion = page.ContentVersion,
                BlocksJson = WikiBlockJson.Serialize([.. WikiBlockJson.ParseBlocks(page.BlocksJson), .. WikiBlockJson.FromMarkdown("- [ ] by hand")])
            }, "someone");
        }

        public async Task<string?> ColumnOfAsync(string boardId, string cardTitle)
        {
            await using var scope = _services.CreateAsyncScope();
            var board = (await scope.ServiceProvider.GetRequiredService<IKanbanBoardService>().GetBoardAsync(Guid.Parse(boardId)))!;
            return board.Columns.FirstOrDefault(column => column.Cards.Any(card => card.Title == cardTitle))?.Label;
        }

        public async Task<Guid> TicketAsync(string subject)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var contact = new Contact { FullName = "Ada Customer" };
            db.Contacts.Add(contact);
            var ticket = new SupportTicket { ContactId = contact.Id, Subject = subject };
            db.SupportTickets.Add(ticket);
            await db.SaveChangesAsync();
            return ticket.Id;
        }

        public async Task SiteAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICmsBuilderService>().SaveSiteAsync(new CmsSiteEditorModel { Name = "Site" });
        }

        public async Task<CmsPage> SitePageAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICmsBuilderService>().GetPageAsync(id))!;
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ScriptedModel(params Func<IReadOnlyList<OllamaChatMessage>, OllamaChatResponse>[] script) : IOllamaService
    {
        private int _next;

        public List<IReadOnlyList<OllamaToolDefinition>> Tools { get; } = [];

        public Task<OllamaChatResponse> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default)
        {
            Tools.Add(tools ?? []);
            _next.Should().BeLessThan(script.Length, "the model was asked more often than the script expects");
            return Task.FromResult(script[_next++](messages.ToList()));
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
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => Task.FromResult(string.Empty);
    }

    private sealed class NoSuiteSearch : ISuiteSearchService
    {
        public Task<IReadOnlyList<SuiteSearchResult>> SearchAsync(string query, string performedBy, int take = 12, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SuiteSearchResult>>([]);

        public Task<IReadOnlyList<SuiteSearchResult>> FindAsync(SuiteSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SuiteSearchResult>>([]);
    }
}
