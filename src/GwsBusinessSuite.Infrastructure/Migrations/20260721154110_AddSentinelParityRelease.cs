using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelParityRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotionId",
                table: "WikiDatabaseViews",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionId",
                table: "SentinelDiscussionComments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTwoWayWrites",
                table: "NotionConnectorSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SelectedNotionIdsJson",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SyncDirection",
                table: "NotionConnectorSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "import");

            migrationBuilder.CreateTable(
                name: "SentinelAiRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReviewedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelAiRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelPresenceLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelPresenceLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelPublicShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsDatabase = table.Column<bool>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AllowSearchIndexing = table.Column<bool>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelPublicShares", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelResourcePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsDatabase = table.Column<bool>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    AccessLevel = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelResourcePermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelWorkspaceMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelWorkspaceMembers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseViews_NotionId",
                table: "WikiDatabaseViews",
                column: "NotionId");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelDiscussionComments_NotionId",
                table: "SentinelDiscussionComments",
                column: "NotionId");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelAiRuns_Status_CreatedAt",
                table: "SentinelAiRuns",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelAiRuns_WikiPageId_CreatedAt",
                table: "SentinelAiRuns",
                columns: new[] { "WikiPageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPresenceLeases_WikiPageId_LastSeenAt",
                table: "SentinelPresenceLeases",
                columns: new[] { "WikiPageId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPublicShares_TargetId_IsDatabase_RevokedAt",
                table: "SentinelPublicShares",
                columns: new[] { "TargetId", "IsDatabase", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPublicShares_TokenHash",
                table: "SentinelPublicShares",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelResourcePermissions_TargetId_IsDatabase_Username",
                table: "SentinelResourcePermissions",
                columns: new[] { "TargetId", "IsDatabase", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelWorkspaceMembers_Username",
                table: "SentinelWorkspaceMembers",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelAiRuns");

            migrationBuilder.DropTable(
                name: "SentinelPresenceLeases");

            migrationBuilder.DropTable(
                name: "SentinelPublicShares");

            migrationBuilder.DropTable(
                name: "SentinelResourcePermissions");

            migrationBuilder.DropTable(
                name: "SentinelWorkspaceMembers");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabaseViews_NotionId",
                table: "WikiDatabaseViews");

            migrationBuilder.DropIndex(
                name: "IX_SentinelDiscussionComments_NotionId",
                table: "SentinelDiscussionComments");

            migrationBuilder.DropColumn(
                name: "NotionId",
                table: "WikiDatabaseViews");

            migrationBuilder.DropColumn(
                name: "NotionId",
                table: "SentinelDiscussionComments");

            migrationBuilder.DropColumn(
                name: "AllowTwoWayWrites",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "SelectedNotionIdsJson",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "SyncDirection",
                table: "NotionConnectorSettings");
        }
    }
}
