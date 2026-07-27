using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotionSyncCoverageDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastSyncContentBlockCount",
                table: "NotionConnectorSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastSyncDiscoveredCount",
                table: "NotionConnectorSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastSyncEmptyContentCount",
                table: "NotionConnectorSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastSyncSkippedCount",
                table: "NotionConnectorSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSyncContentBlockCount",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "LastSyncDiscoveredCount",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "LastSyncEmptyContentCount",
                table: "NotionConnectorSettings");

            migrationBuilder.DropColumn(
                name: "LastSyncSkippedCount",
                table: "NotionConnectorSettings");
        }
    }
}
