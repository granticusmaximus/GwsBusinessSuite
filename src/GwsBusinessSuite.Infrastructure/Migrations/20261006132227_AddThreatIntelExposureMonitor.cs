using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddThreatIntelExposureMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThreatFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    ReferenceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", nullable: true),
                    DigestedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThreatIntelUserStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    LastKevVisitAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatIntelUserStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThreatInvestigations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Query = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExportedWikiPageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatInvestigations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThreatMonitorSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DigestEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DigestRecipient = table.Column<string>(type: "TEXT", nullable: true),
                    DigestHourLocal = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDigestSentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAssetCheckAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastStackCheckAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastDependencyCheckAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatMonitorSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThreatStackItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", nullable: true),
                    Product = table.Column<string>(type: "TEXT", nullable: true),
                    Keywords = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatStackItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThreatWatchAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatWatchAssets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatFindings_Key",
                table: "ThreatFindings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThreatFindings_ResolvedAt_Severity",
                table: "ThreatFindings",
                columns: new[] { "ResolvedAt", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatIntelUserStates_Username",
                table: "ThreatIntelUserStates",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThreatWatchAssets_Value",
                table: "ThreatWatchAssets",
                column: "Value",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThreatFindings");

            migrationBuilder.DropTable(
                name: "ThreatIntelUserStates");

            migrationBuilder.DropTable(
                name: "ThreatInvestigations");

            migrationBuilder.DropTable(
                name: "ThreatMonitorSettings");

            migrationBuilder.DropTable(
                name: "ThreatStackItems");

            migrationBuilder.DropTable(
                name: "ThreatWatchAssets");
        }
    }
}
