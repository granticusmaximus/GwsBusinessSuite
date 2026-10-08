using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCivicWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TriggerCivicBillStatusChanged",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CivicItemSightings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", nullable: false),
                    Desk = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CivicItemSightings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CivicMeetingDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    MeetingDate = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    AiSummary = table.Column<string>(type: "TEXT", nullable: true),
                    SummarizedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CivicMeetingDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CivicWatchSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HomeLabel = table.Column<string>(type: "TEXT", nullable: false),
                    HomeLatitude = table.Column<double>(type: "REAL", nullable: false),
                    HomeLongitude = table.Column<double>(type: "REAL", nullable: false),
                    HomeAddress = table.Column<string>(type: "TEXT", nullable: true),
                    StateCode = table.Column<string>(type: "TEXT", nullable: true),
                    CountyName = table.Column<string>(type: "TEXT", nullable: true),
                    CongressionalDistrict = table.Column<int>(type: "INTEGER", nullable: true),
                    StateSenateDistrict = table.Column<int>(type: "INTEGER", nullable: true),
                    StateHouseDistrict = table.Column<int>(type: "INTEGER", nullable: true),
                    DistrictsResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FederalRegisterKeywords = table.Column<string>(type: "TEXT", nullable: false),
                    GrantKeywords = table.Column<string>(type: "TEXT", nullable: false),
                    AlertsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlertRecipient = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CivicWatchSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchedBills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Jurisdiction = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    BillType = table.Column<string>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Congress = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    LatestStatus = table.Column<string>(type: "TEXT", nullable: false),
                    LatestStatusDate = table.Column<string>(type: "TEXT", nullable: true),
                    OfficialUrl = table.Column<string>(type: "TEXT", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedBills", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CivicItemSightings_ItemKey",
                table: "CivicItemSightings",
                column: "ItemKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CivicMeetingDocuments_Url",
                table: "CivicMeetingDocuments",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchedBills_Jurisdiction_BillType_Number_Congress",
                table: "WatchedBills",
                columns: new[] { "Jurisdiction", "BillType", "Number", "Congress" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CivicItemSightings");

            migrationBuilder.DropTable(
                name: "CivicMeetingDocuments");

            migrationBuilder.DropTable(
                name: "CivicWatchSettings");

            migrationBuilder.DropTable(
                name: "WatchedBills");

            migrationBuilder.DropColumn(
                name: "TriggerCivicBillStatusChanged",
                table: "AutomationWorkflows");
        }
    }
}
