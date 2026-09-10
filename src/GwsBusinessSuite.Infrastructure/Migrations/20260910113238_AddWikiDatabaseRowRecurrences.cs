using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiDatabaseRowRecurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WikiDatabaseRowRecurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiDatabaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiDatabaseRowTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NextRunAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiDatabaseRowRecurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiDatabaseRowRecurrences_WikiDatabaseRowTemplates_WikiDatabaseRowTemplateId",
                        column: x => x.WikiDatabaseRowTemplateId,
                        principalTable: "WikiDatabaseRowTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiDatabaseRowRecurrences_WikiDatabases_WikiDatabaseId",
                        column: x => x.WikiDatabaseId,
                        principalTable: "WikiDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRowRecurrences_NextRunAtUnixSeconds",
                table: "WikiDatabaseRowRecurrences",
                column: "NextRunAtUnixSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRowRecurrences_WikiDatabaseId",
                table: "WikiDatabaseRowRecurrences",
                column: "WikiDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRowRecurrences_WikiDatabaseRowTemplateId",
                table: "WikiDatabaseRowRecurrences",
                column: "WikiDatabaseRowTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WikiDatabaseRowRecurrences");
        }
    }
}
