using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPageVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerUsername",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerificationLapseNotifiedUnix",
                table: "WikiPages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerifiedAtUnix",
                table: "WikiPages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedBy",
                table: "WikiPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VerifiedUntilUnix",
                table: "WikiPages",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerUsername",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "VerificationLapseNotifiedUnix",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUnix",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "VerifiedBy",
                table: "WikiPages");

            migrationBuilder.DropColumn(
                name: "VerifiedUntilUnix",
                table: "WikiPages");
        }
    }
}
