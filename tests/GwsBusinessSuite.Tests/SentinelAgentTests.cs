using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Application.ContentCalendar;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Application.TimeTracking;
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
using Microsoft.Extensions.Options;
using MimeKit;

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
            .And.NotContain(["add_card", "list_boards", "create_site_page_draft", "search_app", "find_contacts", "list_automations"],
                "area tools are offered only when the turn points at them; CRM and app-wide search remain Admin areas");
        var authorMore = asAuthor.Tools[0].Single(tool => tool.Name == SentinelAgentRunner.UseToolsName).Description;
        authorMore.Should().Contain("blog (").And.Contain("boards (").And.Contain("automations (").And.NotContain("crm (").And.NotContain("site (");

        var adminTools = asAdmin.Tools[0].Select(tool => tool.Name).ToList();
        adminTools.Should().Contain(["create_todo_list", "search_app", SentinelAgentRunner.UseToolsName])
            .And.NotContain(["add_card", "create_deal"], "an area's tools are offered when the turn points at it");
        asAdmin.Tools[0].Single(tool => tool.Name == SentinelAgentRunner.UseToolsName).Description.Should()
            .Contain("boards (").And.Contain("crm (").And.Contain("automations (").And.Contain("site (");
    }

    [Fact]
    public async Task SentinelPageTools_ShouldMatchTheUsersResourceAccess_ForSearchReadAndEdit()
    {
        await using var harness = await Harness.CreateAsync();
        var viewOnly = await harness.PageWithAccessAsync("Shared roadmap", Author.Username, SentinelAccessLevels.View);
        var privatePage = await harness.PageWithAccessAsync("Private roadmap", null, null);

        var model = new ScriptedModel(
            _ => Call("find_sentinel_pages", new { query = "roadmap" }),
            messages =>
            {
                var pages = LastResult(messages)["pages"]!.AsArray();
                pages.Select(node => node!["id"]!.GetValue<string>()).Should().Equal(viewOnly.ToString());
                return Call("read_sentinel_page", new { pageId = viewOnly });
            },
            messages =>
            {
                LastResult(messages)["title"]!.GetValue<string>().Should().Be("Shared roadmap");
                return Call("add_to_sentinel_page", new { pageId = viewOnly, content = "Should be denied" });
            },
            messages =>
            {
                LastResult(messages)["error"]!.GetValue<string>().Should().Contain("doesn't have edit access");
                return Call("read_sentinel_page", new { pageId = privatePage });
            },
            messages =>
            {
                LastResult(messages)["error"]!.GetValue<string>().Should().Contain("doesn't have view access");
                return Say("Access stayed scoped to the signed-in user.");
            });

        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Author, "Review the roadmaps", null, model, "test"), _ => Task.CompletedTask, default);
        turn.Actions.Should().BeEmpty("denied mutations must not create action-log entries");
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
        var cardId = (await harness.CardByTitleAsync(boardId, "Write the post")).RowId;
        turn.Actions.Should().OnlyContain(action => action.Link == $"/admin/boards/{boardId}?card={cardId}");
        (await harness.ColumnOfAsync(boardId, "Write the post")).Should().Be("Done");
        await harness.Runner.UndoAsync(turn.Actions[1].Id, Admin, default);
        (await harness.ColumnOfAsync(boardId, "Write the post")).Should().Be("To do");
    }

    [Fact]
    public async Task Boards_ShouldFindReadAndUpdateExistingCards_WithUndo()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var created = await harness.TurnAsync(chat, Admin, "Make a launch board",
            Call("create_board", new { title = "Launch" }), Say("Done."));
        var boardId = created.Actions[0].Link!.Split('/').Last();
        var added = await harness.TurnAsync(chat, Admin, "Add the existing card",
            Call("add_card", new { boardId, title = "Write the launch post", column = "To do" }), Say("Added."));
        added.Actions.Should().ContainSingle();
        var cardId = (await harness.CardByTitleAsync(boardId, "Write the launch post")).RowId.ToString();

        var found = new ScriptedModel(
            _ => Call("find_cards", new { boardId, query = "launch post" }),
            messages =>
            {
                LastResult(messages)["matches"]!.AsArray().Should().ContainSingle();
                return Call("read_card", new { boardId, cardId });
            },
            messages =>
            {
                LastResult(messages)["title"]!.GetValue<string>().Should().Be("Write the launch post");
                return Say("Found it.");
            });
        await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "Find and read the launch post card", null, found, "test"), _ => Task.CompletedTask, default);

        var priority = await harness.TurnAsync(chat, Admin, "Make it high priority",
            Call("update_card", new { boardId, cardId, field = "priority", value = "High" }), Say("Updated."));
        (await harness.CardAsync(boardId, cardId)).Priority!.Label.Should().Be("High");

        var urgent = await harness.TurnAsync(chat, Admin, "Actually make it urgent",
            Call("update_card", new { boardId, cardId, field = "priority", value = "Urgent" }), Say("Updated."));
        var staleUndo = () => harness.Runner.UndoAsync(priority.Actions[0].Id, Admin, default);
        (await staleUndo.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("changed after");

        await harness.Runner.UndoAsync(urgent.Actions[0].Id, Admin, default);
        (await harness.CardAsync(boardId, cardId)).Priority!.Label.Should().Be("High");
        await harness.Runner.UndoAsync(priority.Actions[0].Id, Admin, default);
        (await harness.CardAsync(boardId, cardId)).Priority.Should().BeNull();

        var description = await harness.TurnAsync(chat, Admin, "Add the brief",
            Call("update_card", new { boardId, cardId, field = "description", value = "Draft, review, and publish the launch article." }), Say("Updated."));
        (await harness.CardDescriptionAsync(boardId, cardId)).Should().Contain("Draft, review, and publish");
        await harness.Runner.UndoAsync(description.Actions[0].Id, Admin, default);
        (await harness.CardDescriptionAsync(boardId, cardId)).Should().NotContain("Draft, review, and publish");
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
    public async Task SitePageDrafts_ShouldSupportReplaceTrashAndRestore()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SiteAsync();
        var chat = Guid.NewGuid();
        var created = await harness.TurnAsync(chat, Admin, "Draft an about page",
            Call("create_site_page_draft", new
            {
                title = "About",
                sections = new object[] { new { heading = "Old heading", text = "Old copy" } }
            }), Say("Drafted."));
        var pageId = Guid.Parse(created.Actions[0].Link!.Split('/')[3]);

        var replaced = await harness.TurnAsync(chat, Admin, "Rewrite the draft",
            Call("replace_site_page_draft", new
            {
                pageId,
                title = "About us",
                slug = "about-us",
                sections = new object[] { new { heading = "Our story", text = "New copy" } }
            }), Say("Rewritten."));
        var page = await harness.SitePageAsync(pageId);
        page.Title.Should().Be("About us");
        page.Slug.Should().Be("about-us");
        CmsBuilderJson.ParseLayoutOrEmpty(page.BlocksJson).Sections[0].Label.Should().Be("Our story");

        await harness.Runner.UndoAsync(replaced.Actions[0].Id, Admin, default);
        (await harness.SitePageAsync(pageId)).Title.Should().Be("About");

        var proposed = await harness.TurnAsync(chat, Admin, "Trash the draft",
            Call("trash_site_page", new { pageId }), Say("Waiting for confirmation."));
        var trashed = await harness.Runner.ConfirmAsync(proposed.Actions[0].Id, Admin, approve: true, default);
        (await harness.SitePageAsync(pageId)).TrashedAt.Should().NotBeNull();
        await harness.Runner.UndoAsync(trashed.Id, Admin, default);
        (await harness.SitePageAsync(pageId)).TrashedAt.Should().BeNull();
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
    public async Task RoutineReversibleCrud_ShouldNotBeRolledBack_AfterThreeChanges()
    {
        await using var harness = await Harness.CreateAsync();
        var model = new ScriptedModel(
            _ => Call("create_todo_list", new { title = "A" }),
            _ => Call("create_todo_list", new { title = "B" }),
            _ => Call("create_todo_list", new { title = "C" }),
            _ => new OllamaChatResponse(string.Empty, [
                new OllamaToolCall("create_todo_list", JsonSerializer.Serialize(new { title = "D" })),
                new OllamaToolCall("create_todo_list", JsonSerializer.Serialize(new { title = "E" }))]),
            _ => Say("Created all five lists."));
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Make five lists", null, model, "test"), _ => Task.CompletedTask, default);

        turn.Actions.Should().HaveCount(5).And.OnlyContain(action => action.Status == SentinelAgentActionStatuses.Done);
        foreach (var title in new[] { "A", "B", "C", "D", "E" }) (await harness.PagesTitledAsync(title)).Should().Be(1);
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

    [Fact]
    public async Task Crm_ShouldAddAContactNoteAndDeal_AsThePerson_AndUndoAStageMove()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        string? contactId = null;
        var model = new ScriptedModel(
            _ => Call("create_contact", new { fullName = "Ada Lovelace", email = "ada@example.com", company = "Engines Ltd", followUpDate = "04-11-26" }),
            messages =>
            {
                contactId = LastResult(messages)["contactId"]!.GetValue<string>();
                return Call("add_contact_note", new { contactId, note = "Wants a quote for the redesign." });
            },
            _ => Call("create_deal", new { contactId, title = "Site redesign", valueUsd = "$12,500" }),
            _ => Say("Added Ada, noted the call and opened the deal."));
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "Add Ada, note she wants a quote, open a deal", null, model, "test"), _ => Task.CompletedTask, default);

        turn.Actions.Select(action => action.Summary).Should().Equal(
            "Added the contact 'Ada Lovelace'", "Added a note to 'Ada Lovelace'", "Added the deal 'Site redesign' for 'Ada Lovelace' (Lead, $12,500)");
        var contact = await harness.ContactAsync("ada@example.com");
        contact.CreatedBy.Should().Be("grant", "the CRM records who the agent acted for, not 'unknown'");
        contact.FollowUpDate.Should().Be(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        turn.Actions[0].Link.Should().Be($"/admin/crm/contacts/{contact.Id}");

        var deal = await harness.DealAsync("Site redesign");
        deal.ValueUsd.Should().Be(12_500);
        var moved = await harness.TurnAsync(chat, Admin, "Move it to proposal sent", Call("set_deal_stage", new { dealId = deal.Id, stage = "proposal sent" }), Say("Moved."));
        moved.Actions.Should().ContainSingle().Which.Summary.Should().Be("Moved the deal 'Site redesign' from Lead to ProposalSent");
        (await harness.DealAsync("Site redesign")).Stage.Should().Be(DealStages.ProposalSent);

        await harness.Runner.UndoAsync(moved.Actions[0].Id, Admin, default);
        (await harness.DealAsync("Site redesign")).Stage.Should().Be(DealStages.Lead);
        await harness.Runner.UndoAsync(turn.Actions[1].Id, Admin, default);
        (await harness.NotesAsync(contact.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Crm_ShouldNotAddAnEmailTwice_NorUndoOverALaterEdit_AndTrashOnlyAfterAConfirm()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        await harness.TurnAsync(chat, Admin, "Add Ada", Call("create_contact", new { fullName = "Ada Lovelace", email = "ada@example.com" }), Say("Added."));
        var contact = await harness.ContactAsync("ada@example.com");

        var again = await harness.TurnAsync(chat, Admin, "Add Ada L", Call("create_contact", new { fullName = "Ada L", email = "ADA@example.com" }), Say("She's already there."));
        again.Actions.Should().BeEmpty("the existing contact's id goes back to the model instead");

        var updated = await harness.TurnAsync(chat, Admin, "She's a customer now", Call("update_contact", new { contactId = contact.Id, status = "customer" }), Say("Updated."));
        updated.Actions.Should().ContainSingle().Which.Summary.Should().Be("Updated the contact 'Ada Lovelace' (status Customer)");
        await harness.SetStatusByHandAsync(contact.Id, ContactStatuses.Inactive);
        var undo = () => harness.Runner.UndoAsync(updated.Actions[0].Id, Admin, default);
        (await undo.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*changed since*");

        var trash = await harness.TurnAsync(chat, Admin, "Delete her", Call("trash_contact", new { contactId = contact.Id }), Say("Waiting for your Confirm."));
        trash.Actions.Should().ContainSingle().Which.Summary.Should().Be("Move the CRM contact 'Ada Lovelace' to the trash");
        (await harness.ContactAsync("ada@example.com")).TrashedAt.Should().BeNull("nothing happens before the Confirm");
        await harness.Runner.ConfirmAsync(trash.Actions[0].Id, Admin, approve: true, default);
        (await harness.ContactAsync("ada@example.com")).TrashedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Automations_ShouldBeProposedThenMadeSwitchedOff_AndOnlySwitchedOnOrRunAfterAConfirm()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var nodes = new object[]
        {
            new { name = "Start", typeKey = "core.manualTrigger" },
            new { name = "Greet", typeKey = "core.set", parameters = new { values = new { message = "Hi" } } }
        };
        var proposed = await harness.TurnAsync(chat, Admin, "Make a greeting automation",
            Call("create_automation", new { name = "Greeting", nodes = new object[] { new { name = "Start", typeKey = "core.nope" } } }),
            Call("create_automation", new { name = "Greeting", nodes = JsonSerializer.Serialize(nodes) }),
            Say("It's waiting for your Confirm."));
        var proposal = proposed.Actions.Should().ContainSingle("the unknown step went back to the model, not to the person").Subject;
        proposal.Summary.Should().Be("Make the automation 'Greeting', switched off: Start -> Greet");
        (await harness.WorkflowsAsync()).Should().BeEmpty("nothing is made before the Confirm");

        var made = await harness.Runner.ConfirmAsync(proposal.Id, Admin, approve: true, default);
        var workflow = (await harness.WorkflowsAsync()).Should().ContainSingle().Subject;
        made.Link.Should().Be($"/admin/automation/{workflow.Id}");
        workflow.Status.Should().NotBe(AutomationWorkflowStatuses.Active);
        var graph = await harness.WorkflowAsync(workflow.Id);
        graph.Connections.Should().ContainSingle("steps without connections run in the order given");
        graph.Nodes.Single(node => node.Name == "Greet").ParametersJson.Should().Contain("Hi");

        var on = await harness.TurnAsync(chat, Admin, "Switch it on", Call("turn_on_automation", new { workflowId = workflow.Id }), Say("Waiting."));
        on.Actions.Should().ContainSingle().Which.AwaitingConfirm.Should().BeTrue();
        (await harness.WorkflowAsync(workflow.Id)).Status.Should().NotBe(AutomationWorkflowStatuses.Active);
        await harness.Runner.ConfirmAsync(on.Actions[0].Id, Admin, approve: true, default);
        (await harness.WorkflowAsync(workflow.Id)).Status.Should().Be(AutomationWorkflowStatuses.Active);

        var off = await harness.TurnAsync(chat, Admin, "Switch it off", Call("turn_off_automation", new { workflowId = workflow.Id }), Say("Off."));
        off.Actions.Should().ContainSingle().Which.Summary.Should().Be("Switched off the automation 'Greeting'");
        (await harness.WorkflowAsync(workflow.Id)).Status.Should().Be(AutomationWorkflowStatuses.Inactive);
        await harness.Runner.UndoAsync(off.Actions[0].Id, Admin, default);
        (await harness.WorkflowAsync(workflow.Id)).Status.Should().Be(AutomationWorkflowStatuses.Active);

        var run = await harness.TurnAsync(chat, Admin, "Run it", Call("run_automation", new { workflowId = workflow.Id }), Say("Waiting."));
        run.Actions.Should().ContainSingle().Which.Summary.Should().Be("Run the automation 'Greeting' now");
        var ran = await harness.Runner.ConfirmAsync(run.Actions[0].Id, Admin, approve: true, default);
        ran.Summary.Should().Be("Ran the automation 'Greeting' (it finished)");
        ran.CanUndo.Should().BeFalse("a run can't be taken back");
    }

    [Fact]
    public async Task AutomationTools_ShouldMirrorTheEditorsPerWorkflowGrant_ForNonAdmins()
    {
        await using var harness = await Harness.CreateAsync();
        var shared = await harness.CreateWorkflowAsync("Shared workflow", Author.Username);
        var privateWorkflow = await harness.CreateWorkflowAsync("Private workflow");
        var model = new ScriptedModel(
            _ => Call("list_automations", new { }),
            messages =>
            {
                var rows = LastResult(messages)["automations"]!.AsArray();
                rows.Select(node => node!["id"]!.GetValue<string>()).Should().Equal(shared.ToString());
                return Call("read_automation", new { workflowId = shared });
            },
            messages =>
            {
                LastResult(messages)["name"]!.GetValue<string>().Should().Be("Shared workflow");
                return Call("read_automation", new { workflowId = privateWorkflow });
            },
            messages =>
            {
                LastResult(messages)["error"]!.GetValue<string>().Should().Contain("doesn't have access");
                return Say("Only the shared workflow was accessible.");
            });

        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Author, "Review my automations", null, model, "test"), _ => Task.CompletedTask, default);
        model.Tools[0].Select(tool => tool.Name).Should().Contain(["list_automations", "read_automation", "run_automation"])
            .And.NotContain("create_automation", "only Admin may create a suite automation");
    }

    [Fact]
    public async Task Areas_ShouldBeOfferedWhenTheTurnPointsAtThem_AndUseToolsAddsOneMidTurn()
    {
        await using var harness = await Harness.CreateAsync();
        IReadOnlyList<string> Offered(ScriptedModel model, int call = 0) => model.Tools[call].Select(tool => tool.Name).ToList();

        var asked = new ScriptedModel(_ => Say("ok"));
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Add Ada Lovelace to the CRM", null, asked, "test"), _ => Task.CompletedTask, default);
        Offered(asked).Should().Contain(["find_contacts", "create_deal", "create_todo_list"]).And.NotContain(["update_ticket", "add_card"]);

        var onScreen = new ScriptedModel(_ => Say("ok"));
        var page = GwsBusinessSuite.Application.Assistant.AssistantPrompt.DescribeContext(
            new GwsBusinessSuite.Application.Assistant.AssistantPageContext("Support", "Ticket", "Invoice PDF won't open", RecordId: Guid.NewGuid().ToString()), null);
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Mark this one urgent", page, onScreen, "test"), _ => Task.CompletedTask, default);
        Offered(onScreen).Should().Contain("update_ticket", "the open page is a ticket");

        var chat = Guid.NewGuid();
        await harness.TurnAsync(chat, Admin, "Add Ada to the CRM", Call("create_contact", new { fullName = "Ada Lovelace" }), Say("Added."));
        var later = new ScriptedModel(_ => Say("ok"));
        await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "Make her a customer now", null, later, "test"), _ => Task.CompletedTask, default);
        Offered(later).Should().Contain("update_contact", "this chat already changed something in the CRM");

        var reaching = new ScriptedModel(
            _ => Call(SentinelAgentRunner.UseToolsName, new { area = "support" }),
            _ => Say("I have them now."));
        await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "What's waiting on me?", null, reaching, "test"), _ => Task.CompletedTask, default);
        Offered(reaching, 0).Should().NotContain("find_tickets");
        Offered(reaching, 1).Should().Contain("find_tickets", "use_tools adds the area for the rest of the turn");
    }

    [Fact]
    public async Task Support_ShouldTriageWithAnExactUndo_AndReplyOnlyAfterAConfirm()
    {
        await using var harness = await Harness.CreateAsync();
        var ticketId = await harness.TicketAsync("Invoice PDF won't open");
        var chat = Guid.NewGuid();

        var updated = await harness.TurnAsync(chat, Admin, "Resolve the invoice ticket and make it high priority",
            Call("update_ticket", new { ticketId, status = "resolved", priority = "high" }), Say("Done."));
        updated.Actions.Should().ContainSingle().Which.Summary.Should().Be("Updated the ticket 'Invoice PDF won't open' (Resolved, High, unassigned)");
        (await harness.TicketViewAsync(ticketId)).Status.Should().Be(SupportTicketStatuses.Resolved);
        await harness.Runner.UndoAsync(updated.Actions[0].Id, Admin, default);
        var restored = await harness.TicketViewAsync(ticketId);
        (restored.Status, restored.Priority).Should().Be((SupportTicketStatuses.Open, SupportTicketPriorities.Normal), "Undo puts back both changes, not just the last");

        var reply = await harness.TurnAsync(chat, Admin, "Tell them it's fixed",
            Call("reply_to_ticket", new { ticketId, message = "We've fixed the PDF; please try again." }), Say("Waiting for your Confirm."));
        reply.Actions.Should().ContainSingle().Which.Summary.Should().Be("Reply to 'Invoice PDF won't open' (emails Ada Customer): We've fixed the PDF; please try again.");
        harness.PortalMail.Sent.Should().BeEmpty("nothing is emailed before the Confirm");
        await harness.Runner.ConfirmAsync(reply.Actions[0].Id, Admin, approve: true, default);
        harness.PortalMail.Sent.Should().ContainSingle().Which.To.Should().Be("ada.customer@example.com");
        (await harness.TicketViewAsync(ticketId)).Messages.Should().ContainSingle(message => message.AuthorName == "grant");
    }

    [Fact]
    public async Task Time_ShouldLogAndTimeWorkForAContact_AndUndoAnEntry()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        await harness.TurnAsync(chat, Admin, "Add Ada", Call("create_contact", new { fullName = "Ada Lovelace", email = "ada@example.com" }), Say("Added."));
        var contactId = (await harness.ContactAsync("ada@example.com")).Id;

        var logged = await harness.TurnAsync(chat, Admin, "Log an hour and a half on Ada's design review yesterday",
            Call("log_time", new { contactId, description = "Design review", hours = 1.5, date = "2026-10-29" }), Say("Logged."));
        logged.Actions.Should().ContainSingle().Which.Summary.Should().Be("Logged 1 h 30 min for 'Design review' (Ada Lovelace, 29-10-26)");

        var timed = await harness.TurnAsync(chat, Admin, "Start a timer for Ada's call, then stop it",
            Call("start_timer", new { contactId, description = "Call" }), Call("stop_timer", new { }), Say("Stopped."));
        timed.Actions.Select(action => action.Summary).Should().Equal("Started a timer for 'Call' (Ada Lovelace)", "Stopped the timer for 'Call' (1 min)");
        timed.Actions[1].CanUndo.Should().BeFalse();

        await harness.Runner.UndoAsync(logged.Actions[0].Id, Admin, default);
        (await harness.TimeEntriesAsync()).Should().Equal("Call");
    }

    [Fact]
    public async Task Blog_ShouldDraftAndEditPrivately_PublishOnlyAfterAConfirm_AndUndoBackToDraft()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        await harness.TurnAsync(chat, Author, "Draft a launch post", Call("create_blog_post_draft", new { title = "We launched!", body = "First line." }), Say("Drafted."));
        var second = await harness.TurnAsync(chat, Author, "And another with the same title", Call("create_blog_post_draft", new { title = "We launched!", body = "Second." }), Say("Drafted."));
        var post = await harness.PostAsync("we-launched");
        post.Status.Should().Be(ArticleStatuses.Draft);
        post.CreatedBy.Should().Be("writer");
        (await harness.PostAsync("we-launched-2")).Id.ToString().Should().Be(second.Actions[0].Link!.Split('/').Last());

        var edited = await harness.TurnAsync(chat, Author, "Add a thank-you to the first", Call("edit_blog_post_draft", new { postId = post.Id, addToEnd = "Thank you!" }), Say("Added."));
        (await harness.PostAsync("we-launched")).BodyMarkdown.Should().Be("First line.\n\nThank you!");
        await harness.Runner.UndoAsync(edited.Actions[0].Id, Author, default);
        (await harness.PostAsync("we-launched")).BodyMarkdown.Should().Be("First line.");

        var proposed = await harness.TurnAsync(chat, Author, "Publish it", Call("publish_blog_post", new { postId = post.Id }), Say("Waiting."));
        proposed.Actions.Should().ContainSingle().Which.Summary.Should().StartWith("Publish the blog post 'We launched!' now");
        (await harness.PostAsync("we-launched")).Status.Should().Be(ArticleStatuses.Draft, "nothing goes live before the Confirm");
        var published = await harness.Runner.ConfirmAsync(proposed.Actions[0].Id, Author, approve: true, default);
        published.Link.Should().Be("/blog/we-launched");
        (await harness.PostAsync("we-launched")).Status.Should().Be(ArticleStatuses.Published);

        var editLive = await harness.TurnAsync(chat, Author, "Change its title", Call("edit_blog_post_draft", new { postId = post.Id, title = "Changed" }), Say("Can't."));
        editLive.Actions.Should().BeEmpty("only drafts are edited here");
        await harness.Runner.UndoAsync(published.Id, Author, default);
        (await harness.PostAsync("we-launched")).Status.Should().Be(ArticleStatuses.Draft);
    }

    [Fact]
    public async Task ContentCalendar_ShouldMoveAScheduledPost_AndUndoTheMove()
    {
        await using var harness = await Harness.CreateAsync();
        var at = DateTimeOffset.UtcNow.AddDays(3);
        var postId = await harness.ScheduledPostAsync("Autumn sale", at);
        var newTime = DateTime.Now.Date.AddDays(5).AddHours(9);

        var moved = await harness.TurnAsync(Guid.NewGuid(), Admin, "Push the autumn sale post to 9am in five days",
            Call("reschedule_content", new { kind = "post", id = postId, at = newTime.ToString("yyyy-MM-dd HH:mm") }), Say("Moved."));
        moved.Actions.Should().ContainSingle().Which.Summary.Should().Be($"Moved 'Autumn sale' to {newTime:dd-MM-yy HH:mm}");
        (await harness.PostByIdAsync(postId)).PublishedAt.Should().Be(new DateTimeOffset(newTime).ToUniversalTime());

        await harness.Runner.UndoAsync(moved.Actions[0].Id, Admin, default);
        (await harness.PostByIdAsync(postId)).PublishedAt!.Value.Should().BeCloseTo(at, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EmailSettingsAndMedia_ShouldConfirmWhatLeavesOrChangesTheApp_AndUndoTheRest()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        await harness.TurnAsync(chat, Admin, "Add Ada", Call("create_contact", new { fullName = "Ada Lovelace", email = "ada@example.com" }), Say("Added."));
        var contact = await harness.ContactAsync("ada@example.com");

        var email = await harness.TurnAsync(chat, Admin, "Email Ada the quote",
            Call("send_email", new { contactId = contact.Id, subject = "Your quote", body = "It's $12,500." }), Say("Waiting."));
        email.Actions.Should().ContainSingle().Which.Summary.Should().Be("Email Ada Lovelace <ada@example.com>: 'Your quote'");
        harness.Mail.Sent.Should().BeEmpty();
        await harness.Runner.ConfirmAsync(email.Actions[0].Id, Admin, approve: true, default);
        harness.Mail.Sent.Should().ContainSingle().Which.Subject.Should().Be("Your quote");
        (await harness.NotesAsync(contact.Id)).Should().Be(1, "an email to a contact goes on their activity log");

        var setting = await harness.TurnAsync(chat, Admin, "Show 25 posts per page", Call("change_setting", new { name = "postsPerPage", value = "25" }), Say("Waiting."));
        setting.Actions.Should().ContainSingle().Which.Summary.Should().Be("Change the setting postsPerPage from '12' to '25'");
        var changed = await harness.Runner.ConfirmAsync(setting.Actions[0].Id, Admin, approve: true, default);
        (await harness.SettingsAsync()).PostsPerPage.Should().Be(25);
        await harness.Runner.UndoAsync(changed.Id, Admin, default);
        (await harness.SettingsAsync()).PostsPerPage.Should().Be(12);

        var imageId = await harness.ImageAsync("harbour.jpg");
        var alt = await harness.TurnAsync(chat, Admin, "Describe the harbour image", Call("set_media_alt_text", new { mediaId = imageId, altText = "Boats in the harbour at dusk" }), Say("Done."));
        alt.Actions.Should().ContainSingle().Which.Summary.Should().Be("Wrote alt text for 'harbour.jpg'");
        await harness.Runner.UndoAsync(alt.Actions[0].Id, Admin, default);
        (await harness.ImageAltTextAsync(imageId)).Should().BeEmpty();
    }

    private static string ToolCallReply(string name, object arguments) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments } } } } });

    private static string TextReply(string text) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = text } });

    // 05-11-26, background jobs: a turn on the person's Mac carries on with nobody following it, and
    // one that finishes that way is kept for the bell. One being followed isn't.
    [Fact]
    public async Task AMacTurnThatFinishesWithNobodyFollowing_ShouldBeKeptForTheBell_AndAFollowedOneShouldNot()
    {
        await using var harness = await Harness.CreateAsync();
        var clock = new ManualClock();
        var relay = new SentinelAgentRelay(clock);
        var turns = new SentinelAgentTurns(harness.Runner, relay, clock, NullLogger<SentinelAgentTurns>.Instance);
        var told = new List<string>();
        turns.JobFinishedUnwatched += told.Add;
        var answerNow = new SemaphoreSlim(0);
        using var closeApp = new CancellationTokenSource();
        var mac = Task.Run(async () =>
        {
            while (true)
            {
                if (await relay.TakeAsync("grant", ["sentinelgpt:latest"], closeApp.Token) is not { } call) continue;
                await answerNow.WaitAsync(closeApp.Token);
                relay.Complete("grant", call.Id, TextReply("All done."), null);
            }
        });
        for (var i = 0; i < 200 && relay.ModelsFor("grant").Count == 0; i++) await Task.Delay(10);

        // Started, then the tab closes: nothing polls it while the Mac thinks.
        var (unwatched, _) = turns.Start(Admin, Guid.NewGuid(), "A long job", null, "On the Home page");
        clock.Advance(TimeSpan.FromSeconds(30));
        answerNow.Release();
        for (var i = 0; i < 300 && told.Count == 0; i++) await Task.Delay(10);
        told.Should().Equal(["grant"]);
        turns.FinishedUnwatched("grant").Should().ContainSingle().Which.Should().BeEquivalentTo(new { TurnId = unwatched!.Value, Request = "A long job", Outcome = "All done." });
        turns.FinishedUnwatched("writer").Should().BeEmpty("a finished job is only its starter's");

        // Followed to the end: no bell.
        var (followed, _) = turns.Start(Admin, Guid.NewGuid(), "A watched job", null);
        clock.Advance(TimeSpan.FromSeconds(30));
        turns.Get(followed!.Value, "grant").Should().NotBeNull();
        answerNow.Release();
        for (var i = 0; i < 300 && turns.Get(followed.Value, "grant")?.Status == SentinelAgentTurns.Statuses.Running; i++) await Task.Delay(10);
        await Task.Delay(50);
        turns.FinishedUnwatched("grant").Should().ContainSingle("only the unwatched one");
        closeApp.Cancel();
        await mac.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

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

    private static OllamaChatResponse Calls(params (string Name, object Arguments)[] calls) =>
        new(string.Empty, calls.Select(call => new OllamaToolCall(call.Name, JsonSerializer.Serialize(call.Arguments))).ToList());

    private static string PlanJson(params (string Title, string[] Steps)[] sections) => JsonSerializer.Serialize(new
    {
        understanding = "What was asked",
        approach = "The simplest way",
        not_possible = Array.Empty<string>(),
        sections = sections.Select(section => new { title = section.Title, steps = section.Steps.Select(step => new { @do = step, check = $"{step} is there" }) })
    });

    private const string AllRight = """{"complete": true, "problems": []}""";

    // The id of a list this chat made, from "What this chat has changed" in the system prompt (oldest first).
    private static string MadeListId(IReadOnlyList<OllamaChatMessage> messages, int index = 0) =>
        Regex.Matches(messages[0].Content, @"sentinel-page id ([0-9a-f-]{36})")[index].Groups[1].Value;

    // 05-11-26 (Grant: think about the task, plan it, break it into sections, carry it out, check it).
    [Fact]
    public async Task AJob_ShouldBeThoughtThrough_PlannedInSections_CarriedOutSectionBySection_AndChecked()
    {
        await using var harness = await Harness.CreateAsync();
        var model = new ScriptedModel(
            _ => Call(SentinelAgentRunner.PlanJobToolName, new { request = "Two lists, then sunscreen on the first" }),
            messages =>
            {
                messages[0].Content.Should().Contain("Think the job through").And.Contain("create_todo_list", "the plan is made from real capabilities");
                return Say("Here you go:\n" + PlanJson(("Lists", ["Make the Packing list", "Make the Errands list"]), ("Items", ["Add sunscreen to Packing"])));
            },
            messages =>
            {
                messages[0].Content.Should().Contain("section 1 of 2").And.Contain("☐ 1. Make the Packing list");
                return Calls(("create_todo_list", new { title = "Packing" }), (SentinelAgentRunner.CompleteStepToolName, new { step = 1, result = "made it" }),
                    ("create_todo_list", new { title = "Errands" }), (SentinelAgentRunner.CompleteStepToolName, new { step = 2, result = "made it" }));
            },
            messages =>
            {
                messages[0].Content.Should().Contain("section 2 of 2").And.Contain("☑ 1. Make the Packing list", "a fresh leg carries the ledger");
                return Calls(("add_todo_items", new { pageId = MadeListId(messages), items = new[] { "sunscreen" } }),
                    (SentinelAgentRunner.CompleteStepToolName, new { step = 3, result = "added" }));
            },
            messages =>
            {
                messages[0].Content.Should().Contain("You're checking a finished job");
                return Say(AllRight);
            });

        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Make me a Packing and an Errands list, and put sunscreen on Packing", null, model, "test"),
            _ => Task.CompletedTask, default);

        turn.Answer.Should().Be("Done and checked: 3 steps in 2 sections.");
        (await harness.ToDosAsync("Packing")).Should().Equal("sunscreen");
        (await harness.PagesTitledAsync("Errands")).Should().Be(1);
        var plan = turn.Actions.Single(action => action.Tool == SentinelAgentRunner.PlanToolName);
        plan.Summary.Should().Be("Section 1: Lists\n✓ 1. Make the Packing list\n✓ 2. Make the Errands list\nSection 2: Items\n✓ 3. Add sunscreen to Packing");
        model.Tools[4].Select(tool => tool.Name).Should().NotContain("create_todo_list", "the check only looks").And.Contain("find_sentinel_pages");
    }

    [Fact]
    public async Task RunningOutOfRounds_ShouldBecomeAPlannedJob_NotAFalseDone()
    {
        await using var harness = await Harness.CreateAsync();
        var model = new ScriptedModel(
            _ => Call("create_todo_list", new { title = "A" }),
            _ => Call("create_todo_list", new { title = "B" }),
            messages =>
            {
                messages[1].Content.Should().Contain("Already done in this turn").And.Contain("'A'").And.Contain("'B'");
                return Say(PlanJson(("The rest", ["Make list C"])));
            },
            _ => Calls(("create_todo_list", new { title = "C" }), (SentinelAgentRunner.CompleteStepToolName, new { step = 1, result = "made C" })),
            _ => Say(AllRight));

        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Make lists A, B and C", null, model, "test", MaxRounds: 2),
            _ => Task.CompletedTask, default);

        turn.Answer.Should().Be("Done and checked: 1 step in 1 section.").And.NotContain("Done: Created");
        foreach (var title in new[] { "A", "B", "C" }) (await harness.PagesTitledAsync(title)).Should().Be(1, $"{title} was made once");
    }

    [Fact]
    public async Task AStepThatDoesntCheckOut_ShouldBeRepaired_ThenCheckedAgain()
    {
        await using var harness = await Harness.CreateAsync();
        var model = new ScriptedModel(
            _ => Call(SentinelAgentRunner.PlanJobToolName, new { request = "A Trip list with tickets" }),
            _ => Say(PlanJson(("Trip", ["Make the Trip list with tickets on it"]))),
            _ => Calls(("create_todo_list", new { title = "Trip" }), (SentinelAgentRunner.CompleteStepToolName, new { step = 1, result = "made it" })),
            _ => Say("""{"complete": false, "problems": [{"step": 1, "why": "tickets aren't on it"}]}"""),
            messages =>
            {
                messages[0].Content.Should().Contain("didn't check out").And.Contain("tickets aren't on it");
                return Calls(("add_todo_items", new { pageId = MadeListId(messages), items = new[] { "tickets" } }),
                    (SentinelAgentRunner.CompleteStepToolName, new { step = 1, result = "added tickets" }));
            },
            _ => Say(AllRight));

        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin, "Make a Trip list with tickets on it", null, model, "test"),
            _ => Task.CompletedTask, default);

        turn.Answer.Should().Be("Done and checked: 1 step in 1 section.");
        (await harness.ToDosAsync("Trip")).Should().Equal("tickets");
    }

    [Fact]
    public async Task AJobThatRunsOut_ShouldSayExactlyWhatsLeft_AndCarryOnShouldPickItUp()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var first = new ScriptedModel(
            _ => Call(SentinelAgentRunner.PlanJobToolName, new { request = "Lists X and Y" }),
            _ => Say(PlanJson(("Lists", ["Make list X", "Make list Y"]))),
            _ => Calls(("create_todo_list", new { title = "X" }), (SentinelAgentRunner.CompleteStepToolName, new { step = 1, result = "made X" })),
            _ => Say("I'll stop there."),
            _ => Say(AllRight));
        var partly = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "Make lists X and Y", null, first, "test", MaxRounds: 1),
            _ => Task.CompletedTask, default);
        partly.Answer.Should().Be("Partly done: 1 of 2 steps. Still to do: 2. Make list Y. Say \"carry on\" and I'll pick up from there.");

        var second = new ScriptedModel(
            messages =>
            {
                messages[0].Content.Should().Contain("✓ 1. Make list X").And.Contain("☐ 2. Make list Y");
                return Calls(("create_todo_list", new { title = "Y" }), (SentinelAgentRunner.CompleteStepToolName, new { step = 2, result = "made Y" }));
            },
            _ => Say(AllRight));
        var finished = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "carry on", null, second, "test"), _ => Task.CompletedTask, default);

        finished.Answer.Should().Be("Done and checked: 2 steps in 1 section.");
        (await harness.PagesTitledAsync("X")).Should().Be(1, "carrying on doesn't redo what's done");
        (await harness.PagesTitledAsync("Y")).Should().Be(1);
    }

    [Fact]
    public async Task ABigJob_ShouldWaitForGo_ThenGoShouldCarryOutTheSavedPlan()
    {
        await using var harness = await Harness.CreateAsync();
        var chat = Guid.NewGuid();
        var steps = Enumerable.Range(1, 26).Select(number => $"Step {number}").ToArray();
        var planned = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, "A very big job", null,
            new ScriptedModel(_ => Call(SentinelAgentRunner.PlanJobToolName, new { request = "big" }), _ => Say(PlanJson(("A", steps[..10]), ("B", steps[10..20]), ("C", steps[20..])))), "test"),
            _ => Task.CompletedTask, default);
        var plan = planned.Actions.Single(action => action.Tool == SentinelAgentRunner.PlanToolName);
        plan.AwaitingConfirm.Should().BeTrue("more than 25 steps wait for Go");
        planned.Answer.Should().Contain("Press Go");

        await harness.Runner.ConfirmAsync(plan.Id, Admin, approve: true, default);
        var go = new ScriptedModel(
            _ => Calls(steps[..10].Select((_, index) => ((string, object))(SentinelAgentRunner.SkipStepToolName, new { step = index + 1, why = "not needed" })).ToArray()),
            _ => Calls(steps[10..20].Select((_, index) => ((string, object))(SentinelAgentRunner.SkipStepToolName, new { step = index + 11, why = "not needed" })).ToArray()),
            _ => Calls(steps[20..].Select((_, index) => ((string, object))(SentinelAgentRunner.SkipStepToolName, new { step = index + 21, why = "not needed" })).ToArray()));
        var done = await harness.Runner.RunAsync(new SentinelAgentRequest(chat, Admin, SentinelAgentRunner.GoMessage, null, go, "test"), _ => Task.CompletedTask, default);

        done.Answer.Should().StartWith("I didn't change anything: Step 1 (not needed)");
    }

    // Opt-in live check of a whole job on a real local model (it does nothing unless
    // GWS_LIVE_OLLAMA_MODEL names one, e.g. sentinelgpt:latest; Ollama at 127.0.0.1:11434).
    [Fact]
    public async Task Live_AJobOnARealLocalModel_ShouldBeCarriedOutAndChecked()
    {
        var modelName = Environment.GetEnvironmentVariable("GWS_LIVE_OLLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(modelName)) return;
        await using var harness = await Harness.CreateAsync();
        var steps = new List<string>();
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin,
                "Make a to-do list called 'Trip prep' with passport, tickets and charger on it, and a second to-do list called 'Errands' with milk and stamps.",
                null, new LocalOllama(), modelName),
            agentEvent =>
            {
                if (agentEvent is SentinelAgentStep step) steps.Add(step.Text);
                return Task.CompletedTask;
            }, default);

        var log = string.Join("\n", steps) + "\nANSWER: " + turn.Answer + "\nPLAN: " +
            (turn.Actions.FirstOrDefault(action => action.Tool == SentinelAgentRunner.PlanToolName)?.Summary ?? "(no plan)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gws-live-job.txt"), log);
        (await harness.ToDosAsync("Trip prep")).Should().BeEquivalentTo(["passport", "tickets", "charger"], log);
        (await harness.ToDosAsync("Errands")).Should().BeEquivalentTo(["milk", "stamps"], log);
        turn.Answer.Should().NotStartWith("Partly done", log);
    }

    // More than 8 rounds of work at one change a round: it has to plan, run in sections and check.
    [Fact]
    public async Task Live_ABigJobOnARealLocalModel_ShouldBePlannedCarriedOutInSectionsAndChecked()
    {
        var modelName = Environment.GetEnvironmentVariable("GWS_LIVE_OLLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(modelName)) return;
        await using var harness = await Harness.CreateAsync();
        var steps = new List<string>();
        var lists = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" };
        var turn = await harness.Runner.RunAsync(new SentinelAgentRequest(Guid.NewGuid(), Admin,
                "Make five to-do lists, one each called Monday, Tuesday, Wednesday, Thursday and Friday, each with the item 'stand-up'. " +
                "Then add the item 'review' to every one of those five lists.",
                null, new LocalOllama(), modelName),
            agentEvent =>
            {
                if (agentEvent is SentinelAgentStep step) steps.Add(step.Text);
                return Task.CompletedTask;
            }, default);

        var log = string.Join("\n", steps) + "\nANSWER: " + turn.Answer + "\nPLAN:\n" +
            (turn.Actions.LastOrDefault(action => action.Tool == SentinelAgentRunner.PlanToolName)?.Summary ?? "(no plan)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gws-live-bigjob.txt"), log);
        foreach (var list in lists)
            (await harness.ToDosAsync(list)).Should().BeEquivalentTo(["stand-up", "review"], $"{list}\n{log}");
        turn.Answer.Should().NotStartWith("Partly done", log);
    }

    private sealed class LocalOllama : IOllamaService
    {
        private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(5) };

        public async Task<OllamaChatResponse> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default)
        {
            var payload = OllamaChatPayload.Build(model, messages, tools, "10m").ToJsonString();
            using var response = await Http.PostAsync("api/chat", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);
            return OllamaChatPayload.Parse(await response.Content.ReadAsStringAsync(ct));
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

    [Fact]
    public void ThePlanParser_ShouldTakeTheModelsJson_EvenWithWordsAroundIt_AndKeepItInBounds()
    {
        var many = Enumerable.Range(1, 30).Select(number => $"s{number}").ToArray();
        var job = SentinelAgentRunner.ParseJob("Sure! " + PlanJson(("One", many[..12]), ("Two", many[12..]), ("Empty", [])) + " Hope that helps.", "the request");
        job.Should().NotBeNull();
        job!.Sections.Select(section => section.Steps.Count).Should().Equal([10, 10], "10 steps a section; an empty section is dropped");
        job.Steps.Select(step => step.Number).Should().Equal(Enumerable.Range(1, 20));
        SentinelAgentRunner.ParseJob("no json here", "x").Should().BeNull();
        SentinelAgentRunner.ParseProblems("""{"complete": false, "problems": []}""").Should().BeNull("'not complete' naming nothing can't be acted on");
        SentinelAgentRunner.ParseProblems("""{"complete": false, "problems": [{"step": "2", "why": "missing"}]}""").Should().ContainKey(2);
    }

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
            await using (var db = new ApplicationDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.AppUsers.Add(new AppUser { Username = Admin.Username, Role = AppRoles.Admin, IsActive = true });
                await db.SaveChangesAsync();
            }

            var portalMail = new RecordingPortalMail();
            var mail = new RecordingMail();
            var services = new ServiceCollection()
                .AddScoped(_ => new ApplicationDbContext(options))
                .AddScoped<IAppDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>())
                .AddScoped<IWikiService>(provider => new WikiService(provider.GetRequiredService<IAppDbContext>()))
                .AddScoped<ISentinelAccessService, SentinelAccessService>()
                .AddScoped<IWikiDatabaseService>(provider => new WikiDatabaseService(provider.GetRequiredService<IAppDbContext>()))
                .AddScoped<IKanbanBoardService>(provider => new KanbanBoardService(provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<IWikiDatabaseService>()))
                .AddScoped<ICmsBuilderService>(provider => new CmsBuilderService(provider.GetRequiredService<IAppDbContext>()))
                .AddScoped<ISuiteSearchService, NoSuiteSearch>()
                .AddScoped<ISentinelAgentToolset, SentinelPageAgentTools>()
                .AddScoped<ISentinelAgentToolset, BoardAgentTools>()
                .AddScoped<ISentinelAgentToolset, SitePageAgentTools>()
                .AddScoped<ISentinelAgentToolset>(provider => new ResearchAgentTools(provider.GetRequiredService<ISuiteSearchService>()))
                .AddScoped<ISentinelAgentToolset, MemoryAgentTools>()
                .AddScoped<ActingUser>()
                .AddScoped<ICurrentUserAccessor, ActingUserAccessor>()
                .AddScoped<ICrmService>(provider => new CrmService(provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<ICurrentUserAccessor>()))
                .AddScoped<IAutomationNodeRegistry>(_ => new AutomationNodeRegistry(new NoHttp()))
                .AddScoped<IAutomationWorkflowService>(provider => new AutomationWorkflowService(
                    provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<IAutomationNodeRegistry>(), TimeProvider.System))
                .AddScoped<IAutomationCredentialService>(provider => new AutomationCredentialService(provider.GetRequiredService<IAppDbContext>(), new NoSecrets(), TimeProvider.System))
                .AddScoped<IAutomationExecutionService>(provider => new AutomationExecutionService(
                    provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<IAutomationWorkflowService>(),
                    provider.GetRequiredService<IAutomationNodeRegistry>(), provider.GetRequiredService<IAutomationCredentialService>(), TimeProvider.System))
                .AddScoped<ISentinelAgentToolset, CrmAgentTools>()
                .AddScoped<ISentinelAgentToolset, AutomationAgentTools>()
                .AddSingleton(portalMail)
                .AddSingleton(mail)
                .AddSingleton<IMailTransport>(mail)
                .AddSingleton(Options.Create(new GrowthReportEmailOptions { FromAddress = "suite@example.com" }))
                .AddScoped<ISupportTicketService>(provider => new SupportTicketService(provider.GetRequiredService<IAppDbContext>(), TimeProvider.System,
                    new NoAdminEmail(), portalMail, Options.Create(new SupportNotificationOptions()), NullLogger<SupportTicketService>.Instance))
                .AddScoped<ITimeTrackingService>(provider => new TimeTrackingService(provider.GetRequiredService<IAppDbContext>(), null!, TimeProvider.System))
                .AddScoped<IContentCalendarService>(_ => new ContentCalendarService(new DbFactory(options), TimeProvider.System))
                .AddScoped<ISiteSettingsService>(provider => new SiteSettingsService(provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<ICurrentUserAccessor>()))
                .AddScoped<IMediaLibraryService>(provider => new MediaLibraryService(provider.GetRequiredService<IAppDbContext>(), provider.GetRequiredService<ISiteSettingsService>()))
                .AddScoped<ISentinelAgentToolset, SupportAgentTools>()
                .AddScoped<ISentinelAgentToolset, TimeAgentTools>()
                .AddScoped<ISentinelAgentToolset, BlogAgentTools>()
                .AddScoped<ISentinelAgentToolset, MediaAgentTools>()
                .AddScoped<ISentinelAgentToolset, EmailAgentTools>()
                .AddScoped<ISentinelAgentToolset, SettingsAgentTools>()
                .AddSingleton(TimeProvider.System)
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<SentinelAgentRunner>()
                .BuildServiceProvider();
            return new Harness(connection, services);
        }

        public RecordingPortalMail PortalMail => _services.GetRequiredService<RecordingPortalMail>();

        public RecordingMail Mail => _services.GetRequiredService<RecordingMail>();

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

        public async Task<Guid> PageWithAccessAsync(string title, string? username, string? accessLevel)
        {
            await using var scope = _services.CreateAsyncScope();
            var page = await scope.ServiceProvider.GetRequiredService<IWikiService>().SavePageAsync(
                new WikiPageEditorModel { Title = title, BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(title)) }, Admin.Username);
            if (username is not null && accessLevel is not null)
                await scope.ServiceProvider.GetRequiredService<ISentinelAccessService>().SetPermissionAsync(page.Id, false, username, accessLevel, Admin.Username);
            return page.Id;
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

        public async Task<KanbanCard> CardAsync(string boardId, string cardId)
        {
            await using var scope = _services.CreateAsyncScope();
            var board = (await scope.ServiceProvider.GetRequiredService<IKanbanBoardService>().GetBoardAsync(Guid.Parse(boardId)))!;
            return board.Columns.SelectMany(column => column.Cards).Single(card => card.RowId == Guid.Parse(cardId));
        }

        public async Task<KanbanCard> CardByTitleAsync(string boardId, string title)
        {
            await using var scope = _services.CreateAsyncScope();
            var board = (await scope.ServiceProvider.GetRequiredService<IKanbanBoardService>().GetBoardAsync(Guid.Parse(boardId)))!;
            return board.Columns.SelectMany(column => column.Cards).Single(card => card.Title == title);
        }

        public async Task<string> CardDescriptionAsync(string boardId, string cardId)
        {
            await using var scope = _services.CreateAsyncScope();
            var database = (await scope.ServiceProvider.GetRequiredService<IWikiDatabaseService>().GetDatabaseAsync(Guid.Parse(boardId)))!;
            var row = database.Rows.Single(row => row.Id == Guid.Parse(cardId));
            return SentinelNoteCleanup.ToMarkdown(WikiBlockJson.ParseBlocks(row.BlocksJson));
        }

        public async Task<Guid> TicketAsync(string subject)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var contact = new Contact { FullName = "Ada Customer", Email = "ada.customer@example.com" };
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

        public async Task<Contact> ContactAsync(string email)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Contacts.AsNoTracking().SingleAsync(contact => contact.Email == email);
        }

        public async Task<Deal> DealAsync(string title)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Deals.AsNoTracking().SingleAsync(deal => deal.Title == title);
        }

        public async Task<int> NotesAsync(Guid contactId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().ContactActivities.CountAsync(note => note.ContactId == contactId);
        }

        public async Task SetStatusByHandAsync(Guid contactId, string status)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICrmService>().SetContactStatusAsync(contactId, status);
        }

        public async Task<SupportTicketView> TicketViewAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ISupportTicketService>().GetTicketAsync(id))!;
        }

        public async Task<IReadOnlyList<string>> TimeEntriesAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().TimeEntries.Select(entry => entry.Description).ToListAsync();
        }

        public async Task<Article> PostAsync(string slug)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Articles.AsNoTracking().SingleAsync(article => article.Slug == slug);
        }

        public async Task<Article> PostByIdAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Articles.AsNoTracking().SingleAsync(article => article.Id == id);
        }

        public async Task<Guid> ScheduledPostAsync(string title, DateTimeOffset at)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var post = new Article { Title = title, Slug = "autumn-sale", Status = ArticleStatuses.Published, PublishedAt = at };
            db.Articles.Add(post);
            await db.SaveChangesAsync();
            return post.Id;
        }

        public async Task<SiteSettingsView> SettingsAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISiteSettingsService>().GetSettingsAsync();
        }

        public async Task<Guid> ImageAsync(string fileName)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var image = new MediaAsset { FileName = fileName, ContentType = "image/jpeg", DataUri = "data:image/jpeg;base64,AA==", SizeBytes = 1 };
            db.MediaAssets.Add(image);
            await db.SaveChangesAsync();
            return image.Id;
        }

        public async Task<string> ImageAltTextAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IAppDbContext>().MediaAssets.AsNoTracking().SingleAsync(image => image.Id == id)).AltText;
        }

        public async Task<IReadOnlyList<AutomationWorkflowSummary>> WorkflowsAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAutomationWorkflowService>().ListAsync();
        }

        public async Task<AutomationWorkflowView> WorkflowAsync(Guid id)
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IAutomationWorkflowService>().GetAsync(id))!;
        }

        public async Task<Guid> CreateWorkflowAsync(string name, string? sharedWith = null)
        {
            await using var scope = _services.CreateAsyncScope();
            var node = new AutomationNodeView(Guid.NewGuid(), "Start", "core.manualTrigger", 1, 100, 100, "{}", null, false, false, false, 1, 0, 0, string.Empty);
            var workflow = await scope.ServiceProvider.GetRequiredService<IAutomationWorkflowService>()
                .CreateFromGraphAsync(name, string.Empty, [node], [], Admin.Username);
            if (sharedWith is not null)
                await scope.ServiceProvider.GetRequiredService<ISentinelAccessService>()
                    .SetPermissionAsync(workflow.Id, isDatabase: false, sharedWith, SentinelAccessLevels.View, Admin.Username);
            return workflow.Id;
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

    // What CurrentUserAccessor does first in the app: the person the runner set on the scope.
    private sealed class ActingUserAccessor(ActingUser acting) : ICurrentUserAccessor
    {
        public Task<string> GetCurrentUsernameAsync(CancellationToken cancellationToken = default) => Task.FromResult(acting.Username ?? "unknown");
    }

    private sealed class NoHttp : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class DbFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class NoAdminEmail : IGrowthReportEmailSender
    {
        public GrowthReportDeliveryConfiguration Configuration { get; } = new(true, "ready");
        public Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingPortalMail : IClientPortalEmailSender
    {
        public List<(string To, string Subject)> Sent { get; } = [];

        public Task SendLoginLinkAsync(string toEmail, string contactName, string loginUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendTicketReplyNotificationAsync(string toEmail, string contactName, string ticketSubject, string portalUrl,
            CancellationToken cancellationToken = default, TicketReplyEmail? reply = null)
        {
            Sent.Add((toEmail, ticketSubject));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMail : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];

        public MailRoute Describe(ISmtpTransportOptions options) => new(MailRouteKind.Smtp, "test");

        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoSuiteSearch : ISuiteSearchService
    {
        public Task<IReadOnlyList<SuiteSearchResult>> SearchAsync(string query, string performedBy, int take = 12, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SuiteSearchResult>>([]);

        public Task<IReadOnlyList<SuiteSearchResult>> FindAsync(SuiteSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SuiteSearchResult>>([]);
    }
}
