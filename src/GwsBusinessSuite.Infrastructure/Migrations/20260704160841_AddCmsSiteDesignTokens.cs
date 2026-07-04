using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCmsSiteDesignTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults match the values public-site.css already hardcoded, so existing
            // sites render identically until an admin explicitly picks something else.
            migrationBuilder.AddColumn<string>(
                name: "AccentColorHex",
                table: "CmsSites",
                type: "TEXT",
                nullable: false,
                defaultValue: "#f59e0b");

            migrationBuilder.AddColumn<string>(
                name: "FontPairingKey",
                table: "CmsSites",
                type: "TEXT",
                nullable: false,
                defaultValue: "elegant");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccentColorHex",
                table: "CmsSites");

            migrationBuilder.DropColumn(
                name: "FontPairingKey",
                table: "CmsSites");
        }
    }
}
