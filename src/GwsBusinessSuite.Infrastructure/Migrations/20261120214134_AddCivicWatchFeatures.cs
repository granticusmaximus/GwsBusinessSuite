using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCivicWatchFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarToken",
                table: "CivicWatchUserStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FollowedItems",
                table: "CivicWatchUserStates",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WeeklyPageBuiltAt",
                table: "CivicWatchUserStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CivicMemberVotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Chamber = table.Column<string>(type: "TEXT", nullable: false),
                    MemberId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Party = table.Column<string>(type: "TEXT", nullable: true),
                    RollCallKey = table.Column<string>(type: "TEXT", nullable: false),
                    Measure = table.Column<string>(type: "TEXT", nullable: false),
                    Vote = table.Column<string>(type: "TEXT", nullable: false),
                    PartyMajority = table.Column<string>(type: "TEXT", nullable: true),
                    VotedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CivicMemberVotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CivicWatchUserStates_CalendarToken",
                table: "CivicWatchUserStates",
                column: "CalendarToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CivicMemberVotes_MemberId_RollCallKey",
                table: "CivicMemberVotes",
                columns: new[] { "MemberId", "RollCallKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CivicMemberVotes");

            migrationBuilder.DropIndex(
                name: "IX_CivicWatchUserStates_CalendarToken",
                table: "CivicWatchUserStates");

            migrationBuilder.DropColumn(
                name: "CalendarToken",
                table: "CivicWatchUserStates");

            migrationBuilder.DropColumn(
                name: "FollowedItems",
                table: "CivicWatchUserStates");

            migrationBuilder.DropColumn(
                name: "WeeklyPageBuiltAt",
                table: "CivicWatchUserStates");
        }
    }
}
