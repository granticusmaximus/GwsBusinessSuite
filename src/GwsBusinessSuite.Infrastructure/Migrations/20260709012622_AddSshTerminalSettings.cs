using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSshTerminalSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SshHostKeyFingerprint",
                table: "DigitalOceanSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SshPort",
                table: "DigitalOceanSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SshPrivateKey",
                table: "DigitalOceanSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SshPrivateKeyPassphrase",
                table: "DigitalOceanSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshUsername",
                table: "DigitalOceanSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SshHostKeyFingerprint",
                table: "DigitalOceanSettings");

            migrationBuilder.DropColumn(
                name: "SshPort",
                table: "DigitalOceanSettings");

            migrationBuilder.DropColumn(
                name: "SshPrivateKey",
                table: "DigitalOceanSettings");

            migrationBuilder.DropColumn(
                name: "SshPrivateKeyPassphrase",
                table: "DigitalOceanSettings");

            migrationBuilder.DropColumn(
                name: "SshUsername",
                table: "DigitalOceanSettings");
        }
    }
}
