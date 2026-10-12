using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingNoteLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LinkedBookingId",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkedContactId",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_LinkedBookingId",
                table: "WikiPages",
                column: "LinkedBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_LinkedContactId",
                table: "WikiPages",
                column: "LinkedContactId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WikiPages_LinkedBookingId",
                table: "WikiPages");

            migrationBuilder.DropIndex(
                name: "IX_WikiPages_LinkedContactId",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "LinkedBookingId",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "LinkedContactId",
                table: "WikiPages");
        }
    }
}
