using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLogIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LogIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SampleStack = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstRelease = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    LastRelease = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StatusNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Page = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CircuitId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Release = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogOccurrences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LogIssues_Fingerprint",
                table: "LogIssues",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogIssues_LastSeenUnixMs",
                table: "LogIssues",
                column: "LastSeenUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_LogOccurrences_IssueId_AtUnixMs",
                table: "LogOccurrences",
                columns: new[] { "IssueId", "AtUnixMs" });

            migrationBuilder.CreateIndex(
                name: "IX_LogOccurrences_TraceId",
                table: "LogOccurrences",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogIssues");

            migrationBuilder.DropTable(
                name: "LogOccurrences");
        }
    }
}
