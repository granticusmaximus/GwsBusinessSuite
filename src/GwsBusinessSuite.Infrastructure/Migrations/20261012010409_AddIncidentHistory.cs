using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OverwatchIncidentSightings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    SeverityLevel = table.Column<string>(type: "TEXT", nullable: false),
                    RoadwayName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    FirstSeenUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverwatchIncidentSightings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OverwatchIncidentSightings_Key",
                table: "OverwatchIncidentSightings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OverwatchIncidentSightings_LastSeenUnix_FirstSeenUnix",
                table: "OverwatchIncidentSightings",
                columns: new[] { "LastSeenUnix", "FirstSeenUnix" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OverwatchIncidentSightings");
        }
    }
}
