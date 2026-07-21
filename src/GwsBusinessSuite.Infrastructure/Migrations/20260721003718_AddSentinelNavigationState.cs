using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelNavigationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentinelNavigationEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsDatabase = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastOpenedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelNavigationEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelNavigationEntries_Username_IsDatabase_TargetId",
                table: "SentinelNavigationEntries",
                columns: new[] { "Username", "IsDatabase", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelNavigationEntries_Username_IsFavorite",
                table: "SentinelNavigationEntries",
                columns: new[] { "Username", "IsFavorite" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelNavigationEntries_Username_LastOpenedAt",
                table: "SentinelNavigationEntries",
                columns: new[] { "Username", "LastOpenedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelNavigationEntries");
        }
    }
}
