using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKanbanFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KanbanBoardSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiDatabaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KanbanBoardSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WikiDatabaseRowStatusChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiDatabaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PropertyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromOptionId = table.Column<string>(type: "TEXT", nullable: true),
                    ToOptionId = table.Column<string>(type: "TEXT", nullable: true),
                    AtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiDatabaseRowStatusChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KanbanBoardSettings_WikiDatabaseId",
                table: "KanbanBoardSettings",
                column: "WikiDatabaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRowStatusChanges_RowId",
                table: "WikiDatabaseRowStatusChanges",
                column: "RowId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRowStatusChanges_WikiDatabaseId_AtUnixSeconds",
                table: "WikiDatabaseRowStatusChanges",
                columns: new[] { "WikiDatabaseId", "AtUnixSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KanbanBoardSettings");

            migrationBuilder.DropTable(
                name: "WikiDatabaseRowStatusChanges");
        }
    }
}
