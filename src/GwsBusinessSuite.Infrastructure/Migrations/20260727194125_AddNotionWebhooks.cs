using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotionWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastWebhookEventType",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastWebhookReceivedAt",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WebhookVerificationReceivedAt",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookVerificationToken",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "NotionWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NotionEventId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    EventTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SyncQueued = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotionWebhookEvents", x => x.Id);
                });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotionWebhookEvents");

            migrationBuilder.DropColumn(
                name: "LastWebhookEventType",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "LastWebhookReceivedAt",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "WebhookVerificationReceivedAt",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "WebhookVerificationToken",
                table: "NotionConnectorSettings");
        }
    }
}
