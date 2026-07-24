using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationDatabaseRowChangedTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TriggerWikiDatabaseId",
                table: "AutomationWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationWorkflows_Status_TriggerWikiDatabaseId",
                table: "AutomationWorkflows",
                columns: new[] { "Status", "TriggerWikiDatabaseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutomationWorkflows_Status_TriggerWikiDatabaseId",
                table: "AutomationWorkflows");

            migrationBuilder.DropColumn(
                name: "TriggerWikiDatabaseId",
                table: "AutomationWorkflows");
        }
    }
}
