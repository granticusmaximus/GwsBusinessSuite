using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelDiscussionSelectionAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnchorEnd",
                table: "SentinelDiscussions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnchorStart",
                table: "SentinelDiscussions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnchorText",
                table: "SentinelDiscussions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnchorEnd",
                table: "SentinelDiscussions");

            migrationBuilder.DropColumn(
                name: "AnchorStart",
                table: "SentinelDiscussions");

            migrationBuilder.DropColumn(
                name: "AnchorText",
                table: "SentinelDiscussions");
        }
    }
}
