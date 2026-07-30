using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsGeoLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "WebAnalyticsEvents",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CountryName",
                table: "WebAnalyticsEvents",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RegionCode",
                table: "WebAnalyticsEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RegionName",
                table: "WebAnalyticsEvents",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "WebAnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "CountryName",
                table: "WebAnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "RegionCode",
                table: "WebAnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "RegionName",
                table: "WebAnalyticsEvents");
        }
    }
}
