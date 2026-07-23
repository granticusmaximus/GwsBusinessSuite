using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotionIncrementalSyncWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotionLastEditedAt",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotionLastEditedAt",
                table: "WikiDatabases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotionLastEditedAt",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotionLastEditedAt",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "NotionLastEditedAt",
                table: "WikiDatabases");

            migrationBuilder.DropColumn(
                name: "NotionLastEditedAt",
                table: "WikiDatabaseRows");
        }
    }
}
