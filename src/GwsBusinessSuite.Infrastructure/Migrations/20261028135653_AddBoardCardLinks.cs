using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardCardLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BoardCardId",
                table: "TimeEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BoardCardLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiDatabaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardCardLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_BoardCardId",
                table: "TimeEntries",
                column: "BoardCardId");

            migrationBuilder.CreateIndex(
                name: "IX_BoardCardLinks_Kind_TargetId",
                table: "BoardCardLinks",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_BoardCardLinks_RowId_Kind_TargetId",
                table: "BoardCardLinks",
                columns: new[] { "RowId", "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardCardLinks_WikiDatabaseId",
                table: "BoardCardLinks",
                column: "WikiDatabaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardCardLinks");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_BoardCardId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "BoardCardId",
                table: "TimeEntries");
        }
    }
}
