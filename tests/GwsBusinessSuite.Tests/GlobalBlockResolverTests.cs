using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class GlobalBlockResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldMaterializeGlobalWidgetContent_WithoutChangingPlacementId()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var resolver = new GlobalBlockResolver(globals);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var globalWidget = await globals.CreateWidgetAsync(site.Id, "Promo copy", new LayoutWidget
        {
            Id = "global-widget",
            WidgetType = "paragraph",
            Props = new Dictionary<string, string> { ["text"] = "Synced copy" },
            Style = new WidgetStyle { TextColor = "#2563eb" }
        });

        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    Id = "section-1",
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Id = "column-1",
                            Widgets =
                            [
                                new LayoutWidget
                                {
                                    Id = "placement-widget",
                                    GlobalBlockId = globalWidget.Id,
                                    WidgetType = "paragraph"
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        await resolver.ResolveAsync(site.Id, layout);

        var resolved = layout.Sections[0].Columns[0].Widgets[0];
        resolved.Id.Should().Be("placement-widget");
        resolved.GlobalBlockId.Should().Be(globalWidget.Id);
        resolved.Props["text"].Should().Be("Synced copy");
        resolved.Style.TextColor.Should().Be("#2563eb");
    }

    [Fact]
    public async Task ResolveAsync_ShouldGiveEachGlobalSectionPlacement_UniqueChildWidgetIds()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var resolver = new GlobalBlockResolver(globals);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var nestedWidget = await globals.CreateWidgetAsync(site.Id, "CTA", new LayoutWidget
        {
            Id = "canonical-cta",
            WidgetType = "button",
            Props = new Dictionary<string, string> { ["label"] = "Buy now", ["href"] = "/buy" }
        });

        var globalSection = await globals.CreateSectionAsync(site.Id, "Hero strip", new LayoutSection
        {
            Id = "canonical-section",
            Label = "Hero strip",
            Background = "dark",
            Padding = "lg",
            Columns =
            [
                new LayoutColumn
                {
                    Id = "hero-col",
                    Widgets =
                    [
                        new LayoutWidget
                        {
                            Id = "hero-cta",
                            WidgetType = "button",
                            GlobalBlockId = nestedWidget.Id
                        }
                    ]
                }
            ]
        });

        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection { Id = "placement-a", GlobalBlockId = globalSection.Id },
                new LayoutSection { Id = "placement-b", GlobalBlockId = globalSection.Id }
            ]
        };

        await resolver.ResolveAsync(site.Id, layout);

        var firstWidget = layout.Sections[0].Columns[0].Widgets[0];
        var secondWidget = layout.Sections[1].Columns[0].Widgets[0];

        firstWidget.Id.Should().StartWith("placement-a__");
        secondWidget.Id.Should().StartWith("placement-b__");
        firstWidget.Id.Should().NotBe(secondWidget.Id);
        firstWidget.Props["label"].Should().Be("Buy now");
        secondWidget.Props["label"].Should().Be("Buy now");
    }

    [Fact]
    public async Task SyncSectionAsync_ShouldStoreCanonicalChildWidgetIds_AndKeepNestedGlobalReferencesAsPlaceholders()
    {
        await using var db = await CreateDbAsync();
        var cms = new CmsBuilderService(db);
        var globals = new GlobalBlockService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Globals" });

        var nestedWidget = await globals.CreateWidgetAsync(site.Id, "CTA", new LayoutWidget
        {
            Id = "canonical-cta",
            WidgetType = "button",
            Props = new Dictionary<string, string> { ["label"] = "Start", ["href"] = "/start" }
        });

        var createdSection = await globals.CreateSectionAsync(site.Id, "Hero strip", new LayoutSection
        {
            Id = "canonical-section",
            Label = "Hero strip",
            Columns =
            [
                new LayoutColumn
                {
                    Id = "hero-col",
                    Widgets =
                    [
                        new LayoutWidget
                        {
                            Id = "hero-cta",
                            WidgetType = "button",
                            GlobalBlockId = nestedWidget.Id
                        }
                    ]
                }
            ]
        });

        var placement = GlobalBlockMaterializer.CreateSectionPlacement(createdSection);
        placement.Columns[0].Widgets[0].Props["label"] = "Edited on page";

        var syncedSection = await globals.SyncSectionAsync(site.Id, placement);
        var canonical = CmsBuilderJson.Parse<LayoutSection>(syncedSection.Json);

        canonical.Should().NotBeNull();
        canonical!.Columns.Should().HaveCount(1);
        canonical.Columns[0].Widgets.Should().HaveCount(1);
        canonical.Columns[0].Widgets[0].Id.Should().Be("hero-cta");
        canonical.Columns[0].Widgets[0].GlobalBlockId.Should().Be(nestedWidget.Id);
        canonical.Columns[0].Widgets[0].Props.Should().BeEmpty();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
