using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChainSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    ActorUsername = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    NetworkAddressProtected = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousEventHash = table.Column<string>(type: "TEXT", nullable: false),
                    EventHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_ActorUsername_OccurredAtUnixSeconds",
                table: "SecurityAuditEvents",
                columns: new[] { "ActorUsername", "OccurredAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_Category_OccurredAtUnixSeconds",
                table: "SecurityAuditEvents",
                columns: new[] { "Category", "OccurredAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_ChainSequence",
                table: "SecurityAuditEvents",
                column: "ChainSequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_EventHash",
                table: "SecurityAuditEvents",
                column: "EventHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditEvents_OccurredAtUnixSeconds",
                table: "SecurityAuditEvents",
                column: "OccurredAtUnixSeconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityAuditEvents");
        }
    }
}
