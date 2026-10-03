using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveNotionConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotionConnectorSettings");

            migrationBuilder.DropTable(
                name: "NotionSyncConflicts");

            migrationBuilder.DropTable(
                name: "NotionWebhookEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotionConnectorSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllowTwoWayWrites = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthenticationMode = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "internal"),
                    AutoSyncEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    IntegrationToken = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncArchivedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncContentBlockCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncDiscoveredCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncEmptyContentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncImportedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncSkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncUpdatedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastWebhookEventType = table.Column<string>(type: "TEXT", nullable: true),
                    LastWebhookReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OAuthBotId = table.Column<string>(type: "TEXT", nullable: true),
                    OAuthConnectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OAuthRefreshToken = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    SelectedNotionIdsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    SyncDirection = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "import"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    WebhookVerificationReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    WebhookVerificationToken = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    WorkspaceIconUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspaceName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotionConnectorSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotionSyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    LocalValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    NotionId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    RemoteEditedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RemoteValueJson = table.Column<string>(type: "TEXT", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotionSyncConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotionSyncConflicts_WikiPages_WikiPageId",
                        column: x => x.WikiPageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotionWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    EventTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    NotionEventId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SyncQueued = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspaceId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotionWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotionSyncConflicts_WikiPageId_FieldName_Status",
                table: "NotionSyncConflicts",
                columns: new[] { "WikiPageId", "FieldName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_NotionWebhookEvents_EventTimestamp",
                table: "NotionWebhookEvents",
                column: "EventTimestamp");

            migrationBuilder.CreateIndex(
                name: "IX_NotionWebhookEvents_NotionEventId",
                table: "NotionWebhookEvents",
                column: "NotionEventId",
                unique: true);
        }
    }
}
