using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailTabs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailMessageTabs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageKey = table.Column<string>(type: "TEXT", nullable: false),
                    TabKey = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailMessageTabs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailSenderTabs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", nullable: false),
                    TabKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSenderTabs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailMessageTabs_AccountId_MessageKey",
                table: "MailMessageTabs",
                columns: new[] { "AccountId", "MessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailMessageTabs_Source_AccountId",
                table: "MailMessageTabs",
                columns: new[] { "Source", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_MailSenderTabs_AccountId_Sender",
                table: "MailSenderTabs",
                columns: new[] { "AccountId", "Sender" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailMessageTabs");

            migrationBuilder.DropTable(
                name: "MailSenderTabs");
        }
    }
}
