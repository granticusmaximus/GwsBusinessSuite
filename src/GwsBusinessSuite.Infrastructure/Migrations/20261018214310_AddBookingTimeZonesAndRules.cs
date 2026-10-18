using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingTimeZonesAndRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DaysAhead",
                table: "BookingTypes",
                type: "INTEGER",
                nullable: false,
                // Existing types keep today's behaviour: 14 days ahead, 1 hour notice.
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "MaxPerDay",
                table: "BookingTypes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinNoticeMinutes",
                table: "BookingTypes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "BookingTypes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AttendeeTimeZone",
                table: "Bookings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DaysAhead",
                table: "BookingTypes");

            migrationBuilder.DropColumn(
                name: "MaxPerDay",
                table: "BookingTypes");

            migrationBuilder.DropColumn(
                name: "MinNoticeMinutes",
                table: "BookingTypes");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "BookingTypes");

            migrationBuilder.DropColumn(
                name: "AttendeeTimeZone",
                table: "Bookings");
        }
    }
}
