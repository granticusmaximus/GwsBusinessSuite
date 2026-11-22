using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailSchedulingAndSnooze : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailScheduledSends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    DraftId = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Recipients = table.Column<string>(type: "TEXT", nullable: false),
                    DueUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailScheduledSends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailSnoozes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    UndoJson = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", nullable: false),
                    MessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WakeUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSnoozes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailScheduledSends_NextAttemptUnix_DueUnix",
                table: "MailScheduledSends",
                columns: new[] { "NextAttemptUnix", "DueUnix" });

            migrationBuilder.CreateIndex(
                name: "IX_MailScheduledSends_Username_AccountId",
                table: "MailScheduledSends",
                columns: new[] { "Username", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_MailSnoozes_NextAttemptUnix_WakeUnix",
                table: "MailSnoozes",
                columns: new[] { "NextAttemptUnix", "WakeUnix" });

            migrationBuilder.CreateIndex(
                name: "IX_MailSnoozes_Username_AccountId",
                table: "MailSnoozes",
                columns: new[] { "Username", "AccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailScheduledSends");

            migrationBuilder.DropTable(
                name: "MailSnoozes");
        }
    }
}
