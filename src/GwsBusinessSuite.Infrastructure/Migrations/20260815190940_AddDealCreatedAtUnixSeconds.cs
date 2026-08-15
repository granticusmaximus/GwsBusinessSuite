using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDealCreatedAtUnixSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUnixSeconds",
                table: "Deals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill existing rows from the already-stored CreatedAt (TEXT/ISO-8601 under
            // SQLite) so BI dashboard date-range queries work correctly for deals created
            // before this column existed, not just new ones going forward.
            migrationBuilder.Sql(
                """
                UPDATE "Deals"
                SET "CreatedAtUnixSeconds" = CAST(strftime('%s', "CreatedAt") AS INTEGER);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Deals_CreatedAtUnixSeconds",
                table: "Deals",
                column: "CreatedAtUnixSeconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deals_CreatedAtUnixSeconds",
                table: "Deals");

            migrationBuilder.DropColumn(
                name: "CreatedAtUnixSeconds",
                table: "Deals");
        }
    }
}
