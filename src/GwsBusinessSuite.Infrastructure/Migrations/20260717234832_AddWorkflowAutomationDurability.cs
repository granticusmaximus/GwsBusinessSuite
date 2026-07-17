using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowAutomationDurability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TimeoutMs",
                table: "AutomationNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "HeartbeatAtUnixSeconds",
                table: "AutomationExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingStateJson",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResumeAt",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResumeAtUnixSeconds",
                table: "AutomationExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResumeToken",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitingInputJson",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WaitingNodeId",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitingNodeName",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitingNodeTypeKey",
                table: "AutomationExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_ResumeToken",
                table: "AutomationExecutions",
                column: "ResumeToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_Status_ResumeAtUnixSeconds",
                table: "AutomationExecutions",
                columns: new[] { "Status", "ResumeAtUnixSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_ResumeToken",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_Status_ResumeAtUnixSeconds",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "TimeoutMs",
                table: "AutomationNodes");

            migrationBuilder.DropColumn(
                name: "HeartbeatAtUnixSeconds",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "PendingStateJson",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "ResumeAt",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "ResumeAtUnixSeconds",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "ResumeToken",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "WaitingInputJson",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "WaitingNodeId",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "WaitingNodeName",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "WaitingNodeTypeKey",
                table: "AutomationExecutions");
        }
    }
}
