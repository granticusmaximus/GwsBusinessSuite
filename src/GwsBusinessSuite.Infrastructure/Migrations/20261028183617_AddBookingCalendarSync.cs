using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingCalendarSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingCalendarFeeds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UrlProtected = table.Column<string>(type: "TEXT", nullable: false),
                    UrlHint = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUsername = table.Column<string>(type: "TEXT", nullable: true),
                    AllDayEventsBlock = table.Column<bool>(type: "INTEGER", nullable: false),
                    BusyJson = table.Column<string>(type: "TEXT", nullable: false),
                    BusyCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCalendarFeeds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BookingCalendarSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExportTokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    ExportTokenProtected = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCalendarSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCalendarSettings_ExportTokenHash",
                table: "BookingCalendarSettings",
                column: "ExportTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCalendarFeeds");

            migrationBuilder.DropTable(
                name: "BookingCalendarSettings");
        }
    }
}
