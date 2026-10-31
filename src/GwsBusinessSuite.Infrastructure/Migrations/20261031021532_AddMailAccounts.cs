using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ImapHost = table.Column<string>(type: "TEXT", nullable: false),
                    ImapPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpSecurity = table.Column<string>(type: "TEXT", nullable: false),
                    LoginUsername = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_Username",
                table: "MailAccounts",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailAccounts");
        }
    }
}
