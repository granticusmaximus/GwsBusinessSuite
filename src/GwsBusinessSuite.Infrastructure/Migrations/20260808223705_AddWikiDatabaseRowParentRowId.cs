using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiDatabaseRowParentRowId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentRowId",
                table: "WikiDatabaseRows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiDatabaseRows_ParentRowId",
                table: "WikiDatabaseRows",
                column: "ParentRowId");

            migrationBuilder.AddForeignKey(
                name: "FK_WikiDatabaseRows_WikiDatabaseRows_ParentRowId",
                table: "WikiDatabaseRows",
                column: "ParentRowId",
                principalTable: "WikiDatabaseRows",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WikiDatabaseRows_WikiDatabaseRows_ParentRowId",
                table: "WikiDatabaseRows");

            migrationBuilder.DropIndex(
                name: "IX_WikiDatabaseRows_ParentRowId",
                table: "WikiDatabaseRows");

            migrationBuilder.DropColumn(
                name: "ParentRowId",
                table: "WikiDatabaseRows");
        }
    }
}
