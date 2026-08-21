using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportTicketSlaBreachAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstResponseBreachNotifiedAt",
                table: "SupportTickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionBreachNotifiedAt",
                table: "SupportTickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerSupportTicketSlaBreached",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstResponseBreachNotifiedAt",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "ResolutionBreachNotifiedAt",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "TriggerSupportTicketSlaBreached",
                table: "AutomationWorkflows");
        }
    }
}
