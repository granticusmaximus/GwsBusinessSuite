using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportTicketAutomationTriggersAndSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstResponseDueAt",
                table: "SupportTickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionDueAt",
                table: "SupportTickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerSupportTicketCreated",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerSupportTicketReplied",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstResponseDueAt",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "ResolutionDueAt",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "TriggerSupportTicketCreated",
                table: "AutomationWorkflows");

            migrationBuilder.DropColumn(
                name: "TriggerSupportTicketReplied",
                table: "AutomationWorkflows");
        }
    }
}
