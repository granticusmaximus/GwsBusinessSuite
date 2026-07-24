using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelDatabaseRowIconsCoversHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverImageUrl",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WikiDatabaseRowRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiDatabaseRowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RevisionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BlocksJson = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    CoverImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiDatabaseRowRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiDatabaseRowRevisions_WikiDatabaseRows_WikiDatabaseRowId",
                        column: x => x.WikiDatabaseRowId,
                        principalTable: "WikiDatabaseRows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRowRevisions_WikiDatabaseRowId_RevisionNumber",
                table: "WikiDatabaseRowRevisions",
                columns: new[] { "WikiDatabaseRowId", "RevisionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WikiDatabaseRowRevisions");

            migrationBuilder.DropColumn(
                name: "CoverImageUrl",
                table: "WikiDatabaseRows");

            migrationBuilder.DropColumn(
                name: "Icon",
                table: "WikiDatabaseRows");
        }
    }
}
