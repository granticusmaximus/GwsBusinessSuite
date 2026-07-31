using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMandatoryPortalMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MfaEnabled",
                table: "AppUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaEnrolledAt",
                table: "AppUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MfaLastAcceptedStep",
                table: "AppUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaRecoveryCodeHashesJson",
                table: "AppUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MfaSecretProtected",
                table: "AppUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MfaEnabled",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "MfaEnrolledAt",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "MfaLastAcceptedStep",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "MfaRecoveryCodeHashesJson",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "MfaSecretProtected",
                table: "AppUsers");
        }
    }
}
