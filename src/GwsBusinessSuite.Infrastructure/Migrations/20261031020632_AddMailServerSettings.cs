using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailServerSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailServerSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Security = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    FromAddress = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailServerSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailServerSettings");
        }
    }
}
