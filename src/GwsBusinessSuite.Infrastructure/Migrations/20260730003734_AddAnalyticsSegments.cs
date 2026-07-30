using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyticsSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsSegments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalyticsSegmentRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalyticsSegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Dimension = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsSegmentRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalyticsSegmentRules_AnalyticsSegments_AnalyticsSegmentId",
                        column: x => x.AnalyticsSegmentId,
                        principalTable: "AnalyticsSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsSegmentRules_AnalyticsSegmentId_SortOrder",
                table: "AnalyticsSegmentRules",
                columns: new[] { "AnalyticsSegmentId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsSegments_Name",
                table: "AnalyticsSegments",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsSegmentRules");

            migrationBuilder.DropTable(
                name: "AnalyticsSegments");
        }
    }
}
