using System.Text.Json;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class NotionSyncServiceTests
{
    [Fact]
    public async Task GetSettingsAsync_ShouldNeverReturnTheDecryptedNotionToken()
    {
        await using var fixture = await SyncFixture.CreateAsync();

        var settings = await fixture.Service.GetSettingsAsync();

        settings.Should().NotBeNull();
        settings!.IntegrationToken.Should().BeEmpty();
        settings.HasStoredIntegrationToken.Should().BeTrue();
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldKeepAndValidateStoredTokenWhenReplacementIsBlank()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.ValidatedTokens.Clear();

        var result = await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView
        {
            IntegrationToken = string.Empty,
            AutoSyncEnabled = false
        });

        result.IsSuccess.Should().BeTrue();
        fixture.Notion.ValidatedTokens.Should().Equal("secret");
        (await fixture.Service.GetSettingsAsync())!.HasStoredIntegrationToken.Should().BeTrue();
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldNotReplaceStoredTokenWhenNewTokenFailsValidation()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.ValidationSucceeds = false;

        var result = await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView
        {
            IntegrationToken = "invalid-replacement"
        });

        result.IsSuccess.Should().BeFalse();
        fixture.Notion.ValidationSucceeds = true;
        fixture.Notion.ValidatedTokens.Clear();
        (await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView())).IsSuccess.Should().BeTrue();
        fixture.Notion.ValidatedTokens.Should().Equal("secret");
    }

    [Fact]
    public async Task SyncAsync_ShouldCreateThenUpdateByNotionIdWithoutDuplicating()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "First")];

        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();
        fixture.Notion.SearchResults = [Page("page-1", "Renamed")];
        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();

        var pages = await fixture.Db.WikiPages.Where(p => p.NotionId == "page-1").ToListAsync();
        pages.Should().ContainSingle();
        pages[0].Title.Should().Be("Renamed");
        pages[0].NotionArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task SyncAsync_ShouldSkipBlockAndCommentRequestsForUnchangedPages()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        var editedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        fixture.Notion.SearchResults = [Page("page-1", "First", lastEditedAt: editedAt)];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {"object":"block","id":"paragraph-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Original"}]}}
                """)
        ];
        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();
        fixture.Notion.BlockChildrenRequests.Clear();
        fixture.Notion.CommentRequests.Clear();

        var unchanged = await fixture.Service.SyncAsync();

        unchanged.IsSuccess.Should().BeTrue();
        unchanged.Message.Should().Contain("Skipped 1 unchanged page");
        fixture.Notion.BlockChildrenRequests.Should().BeEmpty();
        fixture.Notion.CommentRequests.Should().BeEmpty();

        fixture.Notion.SearchResults = [Page("page-1", "First", lastEditedAt: editedAt.AddMinutes(1))];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {"object":"block","id":"paragraph-2","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Changed"}]}}
                """)
        ];

        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();
        fixture.Notion.BlockChildrenRequests.Should().Contain("page-1");
        fixture.Notion.CommentRequests.Should().Contain("page-1");
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        WikiBlockJson.ParseBlocks(page.BlocksJson).Should().ContainSingle(block => block.PlainText == "Changed");
    }

    [Fact]
    public async Task SyncAsync_ShouldBootstrapExistingPagesFromTheLastSuccessfulConnectorSync()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        var lastSync = DateTimeOffset.UtcNow.AddMinutes(-5);
        var settings = await fixture.Db.NotionConnectorSettings.SingleAsync();
        settings.LastSyncedAt = lastSync;
        fixture.Db.WikiPages.Add(new GwsBusinessSuite.Domain.Entities.WikiPage
        {
            Title = "Already imported",
            Slug = "already-imported",
            NotionId = "page-1",
            BlocksJson = WikiBlockJson.Serialize(
                [new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Existing")], new Dictionary<string, string>())]),
            CreatedBy = "notion-sync"
        });
        await fixture.Db.SaveChangesAsync();
        var remoteEditedAt = lastSync.AddMinutes(-1);
        fixture.Notion.SearchResults = [Page("page-1", "Already imported", lastEditedAt: remoteEditedAt)];

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("Skipped 1 unchanged page");
        fixture.Notion.BlockChildrenRequests.Should().BeEmpty();
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        page.NotionLastEditedAt.Should().Be(remoteEditedAt);
    }

    [Fact]
    public async Task SyncAsync_ShouldFetchContentForAnEmptyPageInsteadOfBootstrappingItAsCurrent()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        var lastSync = DateTimeOffset.UtcNow.AddMinutes(-5);
        var settings = await fixture.Db.NotionConnectorSettings.SingleAsync();
        settings.LastSyncedAt = lastSync;
        fixture.Db.WikiPages.Add(new GwsBusinessSuite.Domain.Entities.WikiPage
        {
            Title = "Page shell only",
            Slug = "page-shell-only",
            NotionId = "page-1",
            BlocksJson = "[]",
            CreatedBy = "notion-sync"
        });
        await fixture.Db.SaveChangesAsync();
        var remoteEditedAt = lastSync.AddMinutes(-1);
        fixture.Notion.SearchResults = [Page("page-1", "Page shell only", lastEditedAt: remoteEditedAt)];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {"object":"block","id":"paragraph-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Recovered page content"}]}}
                """)
        ];

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        fixture.Notion.BlockChildrenRequests.Should().Contain("page-1");
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        WikiBlockJson.ParseBlocks(page.BlocksJson)
            .Should().ContainSingle(block => block.PlainText == "Recovered page content");
        page.NotionLastEditedAt.Should().Be(remoteEditedAt);
    }

    [Fact]
    public async Task SyncAsync_ShouldAssignUniqueSlugsToNewPagesWithDuplicateTitles()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("page-1", "Untitled"),
            Page("page-2", "Untitled"),
            Page("page-3", "Untitled")
        ];

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var pages = await fixture.Db.WikiPages.OrderBy(page => page.Slug).ToListAsync();
        pages.Select(page => page.Slug).Should().Equal("untitled", "untitled-2", "untitled-3");
    }

    [Fact]
    public async Task SyncAsync_ShouldStopSafelyWhenNotionReturnsNoSharedContent()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Existing")];
        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();
        fixture.Notion.SearchResults = [];

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("no shared pages or databases");
        (await fixture.Db.WikiPages.SingleAsync(page => page.NotionId == "page-1"))
            .NotionArchivedAt.Should().BeNull("loss of integration access must not archive local content");
    }

    [Fact]
    public async Task SyncAsync_ShouldExplainWhenSelectedIdsExcludeAllAccessibleContent()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Accessible")];
        await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView
        {
            SelectedNotionIds = "different-page",
            IntegrationToken = string.Empty
        });

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("none matches the selected");
    }

    [Fact]
    public async Task SyncAsync_ShouldWirePageHierarchyFromParentDescriptor()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("parent", "Parent"),
            Page("child", "Child", "{\"type\":\"page_id\",\"page_id\":\"parent\"}")
        ];

        await fixture.Service.SyncAsync();

        var parent = await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "parent");
        var child = await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "child");
        child.ParentWikiPageId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task SyncAsync_ShouldMirrorNotionChildOrderAcrossPagesAndDatabases()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("parent", "Parent"),
            Page("first", "First", """{"type":"page_id","page_id":"parent"}"""),
            Page("second", "Second", """{"type":"page_id","page_id":"parent"}"""),
            DataSource(
                "projects-source",
                "Projects",
                "projects-container",
                """{"type":"page_id","page_id":"parent"}"""),
            Page("unselected", "Unselected")
        ];
        fixture.Notion.BlockChildren["parent"] =
        [
            Json("""{"object":"block","id":"second","type":"child_page","has_children":false,"child_page":{"title":"Second"}}"""),
            Json("""{"object":"block","id":"projects-container","type":"child_database","has_children":false,"child_database":{"title":"Projects"}}"""),
            Json("""{"object":"block","id":"first","type":"child_page","has_children":false,"child_page":{"title":"First"}}""")
        ];
        await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView
        {
            SelectedNotionIds = "parent"
        });

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        (await fixture.Db.WikiPages.AnyAsync(page => page.NotionId == "unselected")).Should().BeFalse();
        var parent = await fixture.Db.WikiPages.SingleAsync(page => page.NotionId == "parent");
        var first = await fixture.Db.WikiPages.SingleAsync(page => page.NotionId == "first");
        var second = await fixture.Db.WikiPages.SingleAsync(page => page.NotionId == "second");
        var projects = await fixture.Db.WikiDatabases.SingleAsync(database => database.NotionId == "projects-source");

        first.ParentWikiPageId.Should().Be(parent.Id);
        second.ParentWikiPageId.Should().Be(parent.Id);
        projects.ParentWikiPageId.Should().Be(parent.Id, "a data source uses database_parent for its position in the page tree");
        new[]
        {
            (second.Title, second.SortOrder),
            (projects.Title, projects.SortOrder),
            (first.Title, first.SortOrder)
        }.Should().Equal(
            ("Second", 0),
            ("Projects", 1),
            ("First", 2));
    }

    [Fact]
    public async Task SyncAsync_ShouldUseSelectedPageOrderForWorkspaceRoots()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("root-a", "Root A"),
            Page("root-b", "Root B")
        ];
        await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView
        {
            SelectedNotionIds = "root-b, root-a"
        });

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var roots = await fixture.Db.WikiPages
            .Where(page => page.ParentWikiPageId == null)
            .OrderBy(page => page.SortOrder)
            .ToListAsync();
        roots.Select(page => page.NotionId).Should().Equal("root-b", "root-a");
        roots.Select(page => page.SortOrder).Should().Equal(0, 1);
    }

    [Fact]
    public async Task SyncAsync_ShouldIncludeDescendantsOfSelectedTopLevelPages()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("parent", "Parent"),
            Page("child", "Child", "{\"type\":\"page_id\",\"page_id\":\"parent\"}"),
            Page("unselected", "Unselected")
        ];
        fixture.Notion.BlockChildren["child"] =
        [
            Json("""
                {"object":"block","id":"child-paragraph","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Nested page content"}]}}
                """)
        ];
        await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView
        {
            SelectedNotionIds = "parent"
        });

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var pages = await fixture.Db.WikiPages.OrderBy(page => page.Title).ToListAsync();
        pages.Select(page => page.Title).Should().Equal("Child", "Parent");
        var child = pages.Single(page => page.NotionId == "child");
        WikiBlockJson.ParseBlocks(child.BlocksJson)
            .Should().ContainSingle(block => block.PlainText == "Nested page content");
    }

    [Fact]
    public async Task SyncAsync_ShouldRecoverNativeBlocksFromMarkdownWhenStructuredContentReturns404()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Recovered")];
        fixture.Notion.MissingBlockChildren.Add("page-1");
        fixture.Notion.MarkdownPages["page-1"] = new NotionMarkdownPage(
            """
            # Project plan

            This content came from the full-page endpoint.

            - [x] Recovery works

            ```csharp
            Console.WriteLine("Sentinel");
            ```
            """,
            false,
            []);

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("Imported 4 content blocks");
        result.Message.Should().Contain("Recovered 1 page");
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
        blocks.Select(block => block.Type).Should().Equal(
            WikiBlockTypes.Heading1,
            WikiBlockTypes.Paragraph,
            WikiBlockTypes.ToDo,
            WikiBlockTypes.Code);
        blocks.Select(block => block.PlainText).Should().ContainInOrder(
            "Project plan",
            "This content came from the full-page endpoint.",
            "Recovery works",
            "Console.WriteLine(\"Sentinel\");");
    }

    [Fact]
    public async Task SyncAsync_ShouldKeepPageContentWhenOptionalCommentsReturn404()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "No comments capability")];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {"object":"block","id":"paragraph-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Page content survives"}]}}
                """)
        ];
        fixture.Notion.MissingComments.Add("page-1");

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        WikiBlockJson.ParseBlocks(page.BlocksJson)
            .Should().ContainSingle(block => block.PlainText == "Page content survives");
    }

    [Fact]
    public async Task SyncAsync_ShouldPreserveExistingContentWhenAllRemoteContentEndpointsReturn404()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Intermittently unavailable")];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {"object":"block","id":"paragraph-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Previously imported content"}]}}
                """)
        ];
        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();

        fixture.Notion.MissingBlockChildren.Add("page-1");
        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("1 page returned no readable content");
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        WikiBlockJson.ParseBlocks(page.BlocksJson)
            .Should().ContainSingle(block => block.PlainText == "Previously imported content");
    }

    [Fact]
    public async Task SyncAsync_ShouldFlattenTemplateAndUnsupportedContainersWithoutDroppingTheirChildren()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Nested content")];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {"object":"block","id":"template-1","type":"template","has_children":true,"template":{"rich_text":[{"plain_text":"Template"}]}}
                """),
            Json("""
                {"object":"block","id":"unsupported-1","type":"unsupported","has_children":true,"unsupported":{"block_type":"form"}}
                """)
        ];
        fixture.Notion.BlockChildren["template-1"] =
        [
            Json("""
                {"object":"block","id":"template-copy","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Template body"}]}}
                """)
        ];
        fixture.Notion.BlockChildren["unsupported-1"] =
        [
            Json("""
                {"object":"block","id":"unsupported-copy","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Unsupported container body"}]}}
                """)
        ];

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        WikiBlockJson.ParseBlocks(page.BlocksJson).Select(block => block.PlainText)
            .Should().Equal("Template body", "Unsupported container body");
    }

    [Fact]
    public async Task SyncAsync_ShouldImportMeetingNoteSummaryNotesAndTranscript()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Team sync")];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {
                  "object":"block",
                  "id":"meeting-1",
                  "type":"meeting_notes",
                  "has_children":false,
                  "meeting_notes":{
                    "title":[{"plain_text":"Weekly team sync"}],
                    "children":{
                      "summary_block_id":"summary-1",
                      "notes_block_id":"notes-1",
                      "transcript_block_id":"transcript-1"
                    }
                  }
                }
                """)
        ];
        fixture.Notion.BlockChildren["summary-1"] =
        [
            Json("""
                {"object":"block","id":"summary-copy","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Summary content"}]}}
                """)
        ];
        fixture.Notion.BlockChildren["notes-1"] =
        [
            Json("""
                {"object":"block","id":"notes-copy","type":"bulleted_list_item","has_children":false,"bulleted_list_item":{"rich_text":[{"plain_text":"Action item"}]}}
                """)
        ];
        fixture.Notion.BlockChildren["transcript-1"] =
        [
            Json("""
                {"object":"block","id":"transcript-copy","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Transcript content"}]}}
                """)
        ];

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        WikiBlockJson.ParseBlocks(page.BlocksJson).Select(block => block.PlainText)
            .Should().ContainInOrder(
                "Weekly team sync",
                "Summary",
                "Summary content",
                "Notes",
                "Action item",
                "Transcript",
                "Transcript content");
    }

    [Fact]
    public async Task SyncAsync_ShouldSoftArchiveMissingAndUpstreamArchivedPagesAndRestoreReturningPages()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("missing", "Missing"), Page("flagged", "Flagged")];
        await fixture.Service.SyncAsync();

        fixture.Notion.SearchResults = [Page("flagged", "Flagged", archived: true)];
        var archivedResult = await fixture.Service.SyncAsync();

        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "missing")).NotionArchivedAt.Should().NotBeNull();
        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "flagged")).NotionArchivedAt.Should().NotBeNull();
        archivedResult.Archived.Should().Be(2, "one missing page and one explicitly archived page changed state");

        fixture.Notion.SearchResults = [Page("missing", "Returned"), Page("flagged", "Flagged")];
        await fixture.Service.SyncAsync();
        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "missing")).NotionArchivedAt.Should().BeNull();
        (await fixture.Db.WikiPages.SingleAsync(p => p.NotionId == "flagged")).NotionArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task SyncAsync_ShouldExcludeDatabaseRowsFromTheWikiPageTree()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults =
        [
            Page("standalone", "Standalone"),
            Page("row-1", "Database row", "{\"type\":\"database_id\",\"database_id\":\"database-1\"}")
        ];

        await fixture.Service.SyncAsync();

        (await fixture.Db.WikiPages.Select(p => p.NotionId).ToListAsync()).Should().Equal("standalone");
    }

    [Fact]
    public async Task SyncAsync_ShouldImportDatabaseRowPageBlocks()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Database("database-1", "Projects")];
        fixture.Notion.DatabaseSchemas["database-1"] = Json("""
            {"properties":{"Name":{"id":"title","type":"title","title":{}}}}
            """);
        fixture.Notion.DatabaseRows["database-1"] =
        [
            Json("""
                {"object":"page","id":"row-1","parent":{"type":"database_id","database_id":"database-1"},"properties":{"Name":{"id":"title","type":"title","title":[{"plain_text":"First project"}]}}}
                """)
        ];
        fixture.Notion.BlockChildren["row-1"] =
        [
            Json("""
                {"object":"block","id":"block-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Project page notes"}]}}
                """)
        ];

        await fixture.Service.SyncAsync();

        var row = await fixture.Db.WikiDatabaseRows.SingleAsync(item => item.NotionId == "row-1");
        WikiBlockJson.ParseBlocks(row.BlocksJson).Should().ContainSingle(block => block.PlainText == "Project page notes");
    }

    [Fact]
    public async Task SyncAsync_ShouldQueryOnlyChangedDatabaseRowsAndSkipUnchangedRowContent()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        var editedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        fixture.Notion.SearchResults =
        [
            DataSource(
                "database-1",
                "Projects",
                "database-container-1",
                """{"type":"workspace","workspace":true}""",
                editedAt)
        ];
        fixture.Notion.DatabaseSchemas["database-1"] = Json("""
            {"properties":{"Name":{"id":"title","type":"title","title":{}}}}
            """);
        fixture.Notion.DatabaseRows["database-1"] =
        [
            Json("""
                {"object":"page","id":"row-1","last_edited_time":"__EDITED_AT__","parent":{"type":"data_source_id","data_source_id":"database-1"},"properties":{"Name":{"id":"title","type":"title","title":[{"plain_text":"First project"}]}}}
                """.Replace("__EDITED_AT__", editedAt.UtcDateTime.ToString("O"), StringComparison.Ordinal))
        ];
        fixture.Notion.BlockChildren["row-1"] =
        [
            Json("""
                {"object":"block","id":"block-1","type":"paragraph","has_children":false,"paragraph":{"rich_text":[{"plain_text":"Project page notes"}]}}
                """)
        ];
        (await fixture.Service.SyncAsync()).IsSuccess.Should().BeTrue();
        fixture.Notion.BlockChildrenRequests.Clear();
        fixture.Notion.DatabaseSchemaRequests.Clear();
        fixture.Notion.DatabaseEditedAfterRequests.Clear();

        var incremental = await fixture.Service.SyncAsync();

        incremental.IsSuccess.Should().BeTrue();
        incremental.Message.Should().Contain("Skipped 1 unchanged database row");
        fixture.Notion.DatabaseSchemaRequests.Should().BeEmpty();
        fixture.Notion.DatabaseEditedAfterRequests.Should().ContainSingle();
        fixture.Notion.DatabaseEditedAfterRequests.Single().Should().NotBeNull();
        fixture.Notion.BlockChildrenRequests.Should().NotContain("row-1");
    }

    [Fact]
    public async Task SyncAsync_ShouldCacheNotionHostedFilesBehindDurableSentinelUrls()
    {
        await using var fixture = await SyncFixture.CreateAsync();
        fixture.Notion.SearchResults = [Page("page-1", "Files")];
        fixture.Notion.BlockChildren["page-1"] =
        [
            Json("""
                {
                  "object":"block",
                  "id":"file-block-1",
                  "type":"file",
                  "has_children":false,
                  "file":{
                    "type":"file",
                    "name":"Quarterly plan.pdf",
                    "file":{"url":"https://files.example.test/temporary.pdf"},
                    "caption":[{"plain_text":"Quarterly plan"}]
                  }
                }
                """)
        ];
        fixture.Notion.FileDownloads["https://files.example.test/temporary.pdf"] =
            new NotionFileDownload("Quarterly plan.pdf", "application/pdf", [1, 2, 3, 4]);

        var result = await fixture.Service.SyncAsync();

        result.IsSuccess.Should().BeTrue();
        var stored = await fixture.Db.SentinelImportedFiles.SingleAsync();
        stored.NotionBlockId.Should().Be("file-block-1");
        stored.FileName.Should().Be("Quarterly plan.pdf");
        stored.Content.Should().Equal(1, 2, 3, 4);

        var page = await fixture.Db.WikiPages.SingleAsync(item => item.NotionId == "page-1");
        var block = WikiBlockJson.ParseBlocks(page.BlocksJson).Single();
        block.Props["url"].Should().Be($"/admin/sentinel/files/{stored.Id}");
        block.Props["fileName"].Should().Be("Quarterly plan.pdf");
        block.PlainText.Should().Be("Quarterly plan");
    }

    private static JsonElement Page(
        string id,
        string title,
        string parent = "{\"type\":\"workspace\",\"workspace\":true}",
        bool archived = false,
        DateTimeOffset? lastEditedAt = null)
    {
        var page = new Dictionary<string, object?>
        {
            ["object"] = "page",
            ["id"] = id,
            ["archived"] = archived,
            ["parent"] = JsonDocument.Parse(parent).RootElement.Clone(),
            ["properties"] = new Dictionary<string, object?>
            {
                ["Name"] = new Dictionary<string, object?>
                {
                    ["type"] = "title",
                    ["title"] = new[] { new Dictionary<string, object?> { ["plain_text"] = title } }
                }
            }
        };
        if (lastEditedAt is not null)
        {
            page["last_edited_time"] = lastEditedAt.Value.UtcDateTime.ToString("O");
        }
        return JsonSerializer.SerializeToElement(page);
    }

    private static JsonElement Database(string id, string title) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["object"] = "database",
            ["id"] = id,
            ["archived"] = false,
            ["parent"] = new Dictionary<string, object?> { ["type"] = "workspace", ["workspace"] = true },
            ["title"] = new[] { new Dictionary<string, object?> { ["plain_text"] = title } }
        });

    private static JsonElement DataSource(
        string id,
        string title,
        string databaseId,
        string databaseParent,
        DateTimeOffset? lastEditedAt = null)
    {
        var dataSource = new Dictionary<string, object?>
        {
            ["object"] = "data_source",
            ["id"] = id,
            ["in_trash"] = false,
            ["parent"] = new Dictionary<string, object?>
            {
                ["type"] = "database_id",
                ["database_id"] = databaseId
            },
            ["database_parent"] = JsonDocument.Parse(databaseParent).RootElement.Clone(),
            ["title"] = new[] { new Dictionary<string, object?> { ["plain_text"] = title } }
        };
        if (lastEditedAt is not null)
        {
            dataSource["last_edited_time"] = lastEditedAt.Value.UtcDateTime.ToString("O");
        }
        return JsonSerializer.SerializeToElement(dataSource);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class SyncFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SyncFixture(SqliteConnection connection, ApplicationDbContext db, FakeNotionService notion)
        {
            _connection = connection;
            Db = db;
            Notion = notion;
            Service = new NotionSyncService(db, notion, new FakeSecretProtector(), NullLogger<NotionSyncService>.Instance);
        }

        public ApplicationDbContext Db { get; }
        public FakeNotionService Notion { get; }
        public NotionSyncService Service { get; }

        public static async Task<SyncFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var notion = new FakeNotionService();
            var fixture = new SyncFixture(connection, db, notion);
            await fixture.Service.SaveSettingsAsync(new NotionConnectorSettingsView { IntegrationToken = "secret" });
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeNotionService : INotionService
    {
        public List<string> ValidatedTokens { get; } = new();
        public bool ValidationSucceeds { get; set; } = true;
        public IReadOnlyList<JsonElement> SearchResults { get; set; } = [];
        public Dictionary<string, IReadOnlyList<JsonElement>> BlockChildren { get; } = new();
        public HashSet<string> MissingBlockChildren { get; } = [];
        public Dictionary<string, NotionMarkdownPage> MarkdownPages { get; } = new();
        public Dictionary<string, JsonElement> DatabaseSchemas { get; } = new();
        public Dictionary<string, IReadOnlyList<JsonElement>> DatabaseRows { get; } = new();
        public Dictionary<string, NotionFileDownload> FileDownloads { get; } = new();
        public HashSet<string> MissingComments { get; } = [];
        public List<string> BlockChildrenRequests { get; } = [];
        public List<string> CommentRequests { get; } = [];
        public List<DateTimeOffset?> DatabaseEditedAfterRequests { get; } = [];
        public List<string> DatabaseSchemaRequests { get; } = [];

        public Task<NotionValidationResult> ValidateConnectionAsync(string integrationToken, CancellationToken cancellationToken = default)
        {
            ValidatedTokens.Add(integrationToken);
            return Task.FromResult(ValidationSucceeds
                ? new NotionValidationResult(true, "Connected.", "Test Workspace")
                : new NotionValidationResult(false, "Invalid token.", null));
        }

        public Task<NotionPage> SearchAsync(string integrationToken, string? cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotionPage(SearchResults, false, null));

        public Task<NotionPage> GetBlockChildrenAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default)
        {
            BlockChildrenRequests.Add(blockId);
            return MissingBlockChildren.Contains(blockId)
                ? Task.FromException<NotionPage>(new HttpRequestException(
                    "Not found.",
                    null,
                    System.Net.HttpStatusCode.NotFound))
                : Task.FromResult(new NotionPage(BlockChildren.GetValueOrDefault(blockId) ?? [], false, null));
        }

        public Task<JsonElement?> GetPageAsync(string integrationToken, string pageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JsonElement?>(Page(pageId, "Remote"));

        public Task<NotionMarkdownPage?> GetPageMarkdownAsync(string integrationToken, string pageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MarkdownPages.TryGetValue(pageId, out var markdown)
                ? (NotionMarkdownPage?)markdown
                : null);

        public Task<JsonElement?> GetDatabaseAsync(string integrationToken, string databaseId, CancellationToken cancellationToken = default)
        {
            DatabaseSchemaRequests.Add(databaseId);
            return Task.FromResult(DatabaseSchemas.TryGetValue(databaseId, out var schema) ? (JsonElement?)schema : null);
        }

        public Task<NotionPage> QueryDatabaseAsync(
            string integrationToken,
            string databaseId,
            string? cursor,
            DateTimeOffset? editedAfter = null,
            CancellationToken cancellationToken = default)
        {
            DatabaseEditedAfterRequests.Add(editedAfter);
            return Task.FromResult(new NotionPage(DatabaseRows.GetValueOrDefault(databaseId) ?? [], false, null));
        }

        public Task<JsonElement?> GetViewAsync(string integrationToken, string viewId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JsonElement?>(null);

        public Task<NotionPage> ListCommentsAsync(string integrationToken, string blockId, string? cursor, CancellationToken cancellationToken = default)
        {
            CommentRequests.Add(blockId);
            return MissingComments.Contains(blockId)
                ? Task.FromException<NotionPage>(new HttpRequestException(
                    "Not found.",
                    null,
                    System.Net.HttpStatusCode.NotFound))
                : Task.FromResult(new NotionPage([], false, null));
        }

        public Task<NotionFileDownload> DownloadFileAsync(string fileUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(FileDownloads[fileUrl]);

        public Task UpdatePageAsync(string integrationToken, string pageId, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReplaceBlockChildrenAsync(string integrationToken, string blockId, IReadOnlyList<object> children, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"enc::{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["enc::".Length..];
    }
}
