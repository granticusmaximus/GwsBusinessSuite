using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOverwatchAreaWatchNews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Areas saved before this watch news too; their first check learns the current stories
            // quietly (OverwatchAreaService's news baseline), so turning it on sends no backlog.
            migrationBuilder.AddColumn<bool>(
                name: "WatchNews",
                table: "OverwatchWatchAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WatchNews",
                table: "OverwatchWatchAreas");
        }
    }
}
