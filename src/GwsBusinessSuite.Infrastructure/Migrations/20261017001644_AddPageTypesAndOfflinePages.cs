using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPageTypesAndOfflinePages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentinelOfflinePages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelOfflinePages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelPageTypeAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelPageTypeAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelPageTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelPageTypes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelOfflinePages_Username_WikiPageId",
                table: "SentinelOfflinePages",
                columns: new[] { "Username", "WikiPageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPageTypeAssignments_PageTypeId",
                table: "SentinelPageTypeAssignments",
                column: "PageTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPageTypeAssignments_WikiPageId_PageTypeId",
                table: "SentinelPageTypeAssignments",
                columns: new[] { "WikiPageId", "PageTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPageTypes_NormalizedName",
                table: "SentinelPageTypes",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelOfflinePages");

            migrationBuilder.DropTable(
                name: "SentinelPageTypeAssignments");

            migrationBuilder.DropTable(
                name: "SentinelPageTypes");
        }
    }
}
