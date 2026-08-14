using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiPageSystemKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_SystemKey",
                table: "WikiPages",
                column: "SystemKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WikiPages_SystemKey",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "SystemKey",
                table: "WikiPages");
        }
    }
}
