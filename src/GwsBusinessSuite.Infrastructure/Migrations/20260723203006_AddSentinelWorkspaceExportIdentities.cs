using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelWorkspaceExportIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotionExportId",
                table: "WikiPages",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionExportId",
                table: "WikiDatabases",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotionExportId",
                table: "WikiDatabaseRows",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_NotionExportId",
                table: "WikiPages",
                column: "NotionExportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabases_NotionExportId",
                table: "WikiDatabases",
                column: "NotionExportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRows_NotionExportId",
                table: "WikiDatabaseRows",
                column: "NotionExportId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WikiPages_NotionExportId",
                table: "WikiPages");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabases_NotionExportId",
                table: "WikiDatabases");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabaseRows_NotionExportId",
                table: "WikiDatabaseRows");

            migrationBuilder.DropColumn(
                name: "NotionExportId",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "NotionExportId",
                table: "WikiDatabases");

            migrationBuilder.DropColumn(
                name: "NotionExportId",
                table: "WikiDatabaseRows");
        }
    }
}
