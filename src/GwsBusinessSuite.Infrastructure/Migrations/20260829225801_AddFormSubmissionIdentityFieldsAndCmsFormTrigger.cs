using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFormSubmissionIdentityFieldsAndCmsFormTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Company",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContactId",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerCmsFormSubmitted",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Company",
                table: "FormSubmissions");

            migrationBuilder.DropColumn(
                name: "ContactId",
                table: "FormSubmissions");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "FormSubmissions");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "FormSubmissions");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "FormSubmissions");

            migrationBuilder.DropColumn(
                name: "TriggerCmsFormSubmitted",
                table: "AutomationWorkflows");
        }
    }
}
