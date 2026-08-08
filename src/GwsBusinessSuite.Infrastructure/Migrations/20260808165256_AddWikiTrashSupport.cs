using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiTrashSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrashedAt",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrashedAt",
                table: "WikiDatabases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrashedAt",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_TrashedAt",
                table: "WikiPages",
                column: "TrashedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabases_TrashedAt",
                table: "WikiDatabases",
                column: "TrashedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRows_TrashedAt",
                table: "WikiDatabaseRows",
                column: "TrashedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WikiPages_TrashedAt",
                table: "WikiPages");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabases_TrashedAt",
                table: "WikiDatabases");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabaseRows_TrashedAt",
                table: "WikiDatabaseRows");

            migrationBuilder.DropColumn(
                name: "TrashedAt",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "TrashedAt",
                table: "WikiDatabases");

            migrationBuilder.DropColumn(
                name: "TrashedAt",
                table: "WikiDatabaseRows");
        }
    }
}
