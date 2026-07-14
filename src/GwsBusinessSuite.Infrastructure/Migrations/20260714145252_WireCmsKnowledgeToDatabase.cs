using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WireCmsKnowledgeToDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CmsKnowledgeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowSummary = table.Column<string>(type: "TEXT", nullable: false),
                    ImplementationHint = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedBlocksCsv = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmsKnowledgeEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CmsKnowledgeSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LicenseNotes = table.Column<string>(type: "TEXT", nullable: false),
                    UsageGuidance = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmsKnowledgeSources", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "CmsKnowledgeEntries",
                columns: new[] { "Id", "Capability", "CreatedAt", "CreatedBy", "ImplementationHint", "SourceId", "SuggestedBlocksCsv", "UpdatedAt", "UpdatedBy", "WorkflowSummary" },
                values: new object[,]
                {
                    { new Guid("22222222-2222-2222-2222-222222222201"), "Template hierarchy and routing", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Model template precedence in application logic and store template metadata separately from page content.", new Guid("11111111-1111-1111-1111-111111111101"), "template-slot,dynamic-region,route-layout", null, null, "Resolve route to the best matching template with fallback layers." },
                    { new Guid("22222222-2222-2222-2222-222222222202"), "Content revision workflow", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Store immutable revisions and transition events so publish rollback remains safe.", new Guid("11111111-1111-1111-1111-111111111101"), "revision-timeline,approval-gate,publish-status", null, null, "Draft, review, approve, and publish content versions with audit history." },
                    { new Guid("22222222-2222-2222-2222-222222222203"), "Visual section/column composition", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Use JSON schema versioning for block trees and validate depth/width constraints.", new Guid("11111111-1111-1111-1111-111111111102"), "section,column,widget-container", null, null, "Construct pages from nested sections, columns, and widget blocks." },
                    { new Guid("22222222-2222-2222-2222-222222222204"), "Responsive style controls", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Store style settings as breakpoint maps with a deterministic fallback chain.", new Guid("11111111-1111-1111-1111-111111111102"), "responsive-style,breakpoint-rule,visibility-toggle", null, null, "Define per-breakpoint spacing, typography, and visibility controls." },
                    { new Guid("22222222-2222-2222-2222-222222222205"), "Plugin-like extension points", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "Add capability registration contracts and sandbox execution boundaries.", new Guid("11111111-1111-1111-1111-111111111101"), "extension-hook,capability-registration,feature-toggle", null, null, "Allow modular feature packs to register capabilities without core rewrites." }
                });

            migrationBuilder.InsertData(
                table: "CmsKnowledgeSources",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "Key", "LicenseNotes", "Name", "UpdatedAt", "UpdatedBy", "UsageGuidance" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111101"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "wp-clean-room", "Do not copy source code or proprietary assets. Reimplement behavior only.", "WordPress Workflow Reference (Clean Room)", null, null, "Use as product behavior inspiration for workflows, content modeling, and admin UX." },
                    { new Guid("11111111-1111-1111-1111-111111111102"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "seed", "elementor-clean-room", "Do not clone protected UI/brand assets. Build original controls and layouts.", "Elementor Workflow Reference (Clean Room)", null, null, "Use as inspiration for visual editing flow, section nesting, and style controls." }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CmsKnowledgeEntries_SourceId",
                table: "CmsKnowledgeEntries",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_CmsKnowledgeSources_Key",
                table: "CmsKnowledgeSources",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CmsKnowledgeEntries");

            migrationBuilder.DropTable(
                name: "CmsKnowledgeSources");
        }
    }
}
