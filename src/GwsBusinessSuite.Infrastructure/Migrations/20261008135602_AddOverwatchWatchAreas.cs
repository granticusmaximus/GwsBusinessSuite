using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOverwatchWatchAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OverwatchAreaAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    AreaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverwatchAreaAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OverwatchWatchAreas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    North = table.Column<double>(type: "REAL", nullable: false),
                    South = table.Column<double>(type: "REAL", nullable: false),
                    East = table.Column<double>(type: "REAL", nullable: false),
                    West = table.Column<double>(type: "REAL", nullable: false),
                    WatchIncidents = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatchWeather = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatchHazards = table.Column<bool>(type: "INTEGER", nullable: false),
                    MinSeverity = table.Column<string>(type: "TEXT", nullable: false),
                    SeenKeysJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverwatchWatchAreas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OverwatchAreaAlerts_Username_ReadAt",
                table: "OverwatchAreaAlerts",
                columns: new[] { "Username", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OverwatchWatchAreas_Username",
                table: "OverwatchWatchAreas",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OverwatchAreaAlerts");

            migrationBuilder.DropTable(
                name: "OverwatchWatchAreas");
        }
    }
}
