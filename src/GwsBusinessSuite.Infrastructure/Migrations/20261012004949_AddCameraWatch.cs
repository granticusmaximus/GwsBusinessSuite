using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CameraId",
                table: "OverwatchAreaAlerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkUrl",
                table: "OverwatchAreaAlerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OverwatchCameraWatchStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    CameraId = table.Column<string>(type: "TEXT", nullable: false),
                    CameraName = table.Column<string>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    LastLevel = table.Column<string>(type: "TEXT", nullable: false),
                    LastSummary = table.Column<string>(type: "TEXT", nullable: false),
                    LastCheckedUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAlertUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OverwatchCameraWatchStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OverwatchCameraWatchStates_Username_CameraId",
                table: "OverwatchCameraWatchStates",
                columns: new[] { "Username", "CameraId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OverwatchCameraWatchStates");

            migrationBuilder.DropColumn(
                name: "CameraId",
                table: "OverwatchAreaAlerts");

            migrationBuilder.DropColumn(
                name: "LinkUrl",
                table: "OverwatchAreaAlerts");
        }
    }
}
