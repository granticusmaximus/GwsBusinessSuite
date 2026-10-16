using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelEmailIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentinelInboundEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    FromAddress = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AttachmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelInboundEmails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelMailboxSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    AuthServId = table.Column<string>(type: "TEXT", nullable: false),
                    LastPollUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelMailboxSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelInboundEmails_MessageId",
                table: "SentinelInboundEmails",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelInboundEmails_ProcessedAtUnix",
                table: "SentinelInboundEmails",
                column: "ProcessedAtUnix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelInboundEmails");

            migrationBuilder.DropTable(
                name: "SentinelMailboxSettings");
        }
    }
}
