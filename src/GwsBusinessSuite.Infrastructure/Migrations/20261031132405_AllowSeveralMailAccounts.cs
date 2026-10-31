using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowSeveralMailAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailAccounts_Username",
                table: "MailAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_Username",
                table: "MailAccounts",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailAccounts_Username",
                table: "MailAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_Username",
                table: "MailAccounts",
                column: "Username",
                unique: true);
        }
    }
}
