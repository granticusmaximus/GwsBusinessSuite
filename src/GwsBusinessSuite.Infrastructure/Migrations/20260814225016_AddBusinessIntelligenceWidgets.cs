using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessIntelligenceWidgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessIntelligenceWidgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    QueryShape = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Metric = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Dimension = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Visualization = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RangeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessIntelligenceWidgets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessIntelligenceWidgets_OwnerUsername_SortOrder",
                table: "BusinessIntelligenceWidgets",
                columns: new[] { "OwnerUsername", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessIntelligenceWidgets");
        }
    }
}
