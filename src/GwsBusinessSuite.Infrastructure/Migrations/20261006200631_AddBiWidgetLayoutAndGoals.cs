using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBiWidgetLayoutAndGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GoalIsCeiling",
                table: "BusinessIntelligenceWidgets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GoalLastMet",
                table: "BusinessIntelligenceWidgets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GoalValue",
                table: "BusinessIntelligenceWidgets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsWide",
                table: "BusinessIntelligenceWidgets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerBiGoalCrossed",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoalIsCeiling",
                table: "BusinessIntelligenceWidgets");

            migrationBuilder.DropColumn(
                name: "GoalLastMet",
                table: "BusinessIntelligenceWidgets");

            migrationBuilder.DropColumn(
                name: "GoalValue",
                table: "BusinessIntelligenceWidgets");

            migrationBuilder.DropColumn(
                name: "IsWide",
                table: "BusinessIntelligenceWidgets");

            migrationBuilder.DropColumn(
                name: "TriggerBiGoalCrossed",
                table: "AutomationWorkflows");
        }
    }
}
