using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMoreCmsKnowledgeEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "CmsKnowledgeEntries",
                columns: new[] { "Id", "Capability", "CreatedAt", "CreatedBy", "ImplementationHint", "SourceId", "SuggestedBlocksCsv", "UpdatedAt", "UpdatedBy", "WorkflowSummary" },
                values: new object[,]
                {
                    { new Guid("22222222-2222-2222-2222-22222222020a"), "Widget areas / sidebars", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Define named regions per template/site, and let the same widget vocabulary used for page content be dropped into a region - don't invent a parallel widget system just for regions.", new Guid("11111111-1111-1111-1111-111111111101"), "widget-area,sidebar-region,region-slot", null, null, "Named, swappable content regions that live outside the main page flow (e.g. a blog sidebar) and can hold any widget independent of the page's own content." },
                    { new Guid("22222222-2222-2222-2222-22222222020b"), "Conditional display rules", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Attach a small rule set to a layout node evaluated at render time, defaulting to \"always visible\" so existing content renders unchanged when no rule is set.", new Guid("11111111-1111-1111-1111-111111111102"), "display-condition,role-visibility,scheduled-visibility", null, null, "Show or hide a section/widget based on device size, logged-in role, or a date range, rather than it always rendering." },
                    { new Guid("22222222-2222-2222-2222-222222222206"), "Dynamic content loops", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Parameterize source/filter/sort/limit on the widget and re-query at render time so the block always reflects current data - never bake the list into stored page content.", new Guid("11111111-1111-1111-1111-111111111101"), "posts-grid,query-loop,content-source-picker", null, null, "Pull a filtered, ordered, live list of content items into a page instead of hand-placing each one." },
                    { new Guid("22222222-2222-2222-2222-222222222207"), "Popup builder with trigger rules", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Model a popup as its own small layout document plus a separate trigger-rule record, and render it through the same block renderer used for regular pages so widget support stays in sync automatically.", new Guid("11111111-1111-1111-1111-111111111102"), "popup,trigger-rule,modal-overlay", null, null, "A modal with its own mini layout tree, shown based on a trigger condition (page load delay, exit intent, click, scroll depth) rather than being embedded inline in the page." },
                    { new Guid("22222222-2222-2222-2222-222222222208"), "Custom fields / structured metadata", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Store as a JSON dictionary column with a lightweight per-content-type field-definition list, so new fields stay both renderable and editable without ad hoc columns.", new Guid("11111111-1111-1111-1111-111111111101"), "custom-field,field-schema,meta-panel", null, null, "Attach arbitrary typed key-value fields to a content item beyond its fixed schema, without a database migration per field." },
                    { new Guid("22222222-2222-2222-2222-222222222209"), "Theme builder: reusable header/footer templates", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Model header/footer as their own special-purpose layout documents stored per-site and merged into the page at render time, ahead of/after the page's own sections.", new Guid("11111111-1111-1111-1111-111111111102"), "site-header,site-footer,template-part", null, null, "Define a header and footer once per site, edited like any other block layout, and have every page pull from it instead of each page carrying its own nav rendering." }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "CmsKnowledgeEntries",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-22222222020a"));

            migrationBuilder.DeleteData(
                table: "CmsKnowledgeEntries",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-22222222020b"));

            migrationBuilder.DeleteData(
                table: "CmsKnowledgeEntries",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222206"));

            migrationBuilder.DeleteData(
                table: "CmsKnowledgeEntries",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222207"));

            migrationBuilder.DeleteData(
                table: "CmsKnowledgeEntries",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222208"));

            migrationBuilder.DeleteData(
                table: "CmsKnowledgeEntries",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222209"));
        }
    }
}
