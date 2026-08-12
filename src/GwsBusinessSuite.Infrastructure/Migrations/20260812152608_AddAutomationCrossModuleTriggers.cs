using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationCrossModuleTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TriggerCmsPagePublished",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerCrmDealStageChanged",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerCmsPagePublished",
                table: "AutomationWorkflows");

            migrationBuilder.DropColumn(
                name: "TriggerCrmDealStageChanged",
                table: "AutomationWorkflows");
        }
    }
}
