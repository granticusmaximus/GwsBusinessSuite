using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeFormSubmissionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Email",
                table: "FormSubmissions");

            migrationBuilder.DropColumn(
                name: "Message",
                table: "FormSubmissions");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "FormSubmissions",
                newName: "FieldsJson");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FieldsJson",
                table: "FormSubmissions",
                newName: "Name");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "FormSubmissions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
