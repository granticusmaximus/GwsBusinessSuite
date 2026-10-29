using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardNotificationQuiet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Quiet",
                table: "BoardNotifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Before this column, a quietly recorded note was one read the moment it was made.
            migrationBuilder.Sql("UPDATE BoardNotifications SET Quiet = 1 WHERE ReadAt IS NOT NULL AND ReadAt = CreatedAt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Quiet",
                table: "BoardNotifications");
        }
    }
}
