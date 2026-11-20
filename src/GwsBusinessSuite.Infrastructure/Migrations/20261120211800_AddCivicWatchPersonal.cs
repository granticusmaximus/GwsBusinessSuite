using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCivicWatchPersonal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MentionTerms",
                table: "CivicWatchSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "CivicWatchSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "CivicMeetingDocuments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CivicWatchUserStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    LastVisitedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PreviousVisitAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CivicWatchUserStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CivicWatchSettings_Username",
                table: "CivicWatchSettings",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_CivicWatchUserStates_Username",
                table: "CivicWatchUserStates",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CivicWatchUserStates");

            migrationBuilder.DropIndex(
                name: "IX_CivicWatchSettings_Username",
                table: "CivicWatchSettings");

            migrationBuilder.DropColumn(
                name: "MentionTerms",
                table: "CivicWatchSettings");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "CivicWatchSettings");

            migrationBuilder.DropColumn(
                name: "Text",
                table: "CivicMeetingDocuments");
        }
    }
}
