using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotionSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotionArchivedAt",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionId",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotionArchivedAt",
                table: "WikiDatabases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionId",
                table: "WikiDatabases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotionArchivedAt",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionId",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionId",
                table: "WikiDatabaseProperties",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotionConnectorSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IntegrationToken = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceName = table.Column<string>(type: "TEXT", nullable: true),
                    AutoSyncEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSyncImportedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncUpdatedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncArchivedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotionConnectorSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_NotionId",
                table: "WikiPages",
                column: "NotionId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabases_NotionId",
                table: "WikiDatabases",
                column: "NotionId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRows_NotionId",
                table: "WikiDatabaseRows",
                column: "NotionId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseProperties_NotionId",
                table: "WikiDatabaseProperties",
                column: "NotionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotionConnectorSettings");

            migrationBuilder.DropIndex(
                name: "IX_WikiPages_NotionId",
                table: "WikiPages");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabases_NotionId",
                table: "WikiDatabases");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabaseRows_NotionId",
                table: "WikiDatabaseRows");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabaseProperties_NotionId",
                table: "WikiDatabaseProperties");

            migrationBuilder.DropColumn(
                name: "NotionArchivedAt",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "NotionId",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "NotionArchivedAt",
                table: "WikiDatabases");

            migrationBuilder.DropColumn(
                name: "NotionId",
                table: "WikiDatabases");

            migrationBuilder.DropColumn(
                name: "NotionArchivedAt",
                table: "WikiDatabaseRows");

            migrationBuilder.DropColumn(
                name: "NotionId",
                table: "WikiDatabaseRows");

            migrationBuilder.DropColumn(
                name: "NotionId",
                table: "WikiDatabaseProperties");
        }
    }
}
