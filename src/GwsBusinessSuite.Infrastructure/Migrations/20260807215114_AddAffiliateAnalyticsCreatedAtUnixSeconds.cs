using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAffiliateAnalyticsCreatedAtUnixSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUnixSeconds",
                table: "CjCommissionRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUnixSeconds",
                table: "ArticleAffiliateClicks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill from the existing CreatedAt column so pre-existing rows don't fall
            // outside AffiliateAnalyticsService.GetDashboardAsync's new 90-day window the
            // moment this deploys - without this, the dashboard would show nothing until new
            // clicks/commissions started arriving after the migration ran.
            migrationBuilder.Sql(
                """
                UPDATE "CjCommissionRecords"
                SET "CreatedAtUnixSeconds" = CAST(strftime('%s', "CreatedAt") AS INTEGER);
                """);
            migrationBuilder.Sql(
                """
                UPDATE "ArticleAffiliateClicks"
                SET "CreatedAtUnixSeconds" = CAST(strftime('%s', "CreatedAt") AS INTEGER);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CjCommissionRecords_CreatedAtUnixSeconds",
                table: "CjCommissionRecords",
                column: "CreatedAtUnixSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateClicks_CreatedAtUnixSeconds",
                table: "ArticleAffiliateClicks",
                column: "CreatedAtUnixSeconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CjCommissionRecords_CreatedAtUnixSeconds",
                table: "CjCommissionRecords");

            migrationBuilder.DropIndex(
                name: "IX_ArticleAffiliateClicks_CreatedAtUnixSeconds",
                table: "ArticleAffiliateClicks");

            migrationBuilder.DropColumn(
                name: "CreatedAtUnixSeconds",
                table: "CjCommissionRecords");

            migrationBuilder.DropColumn(
                name: "CreatedAtUnixSeconds",
                table: "ArticleAffiliateClicks");
        }
    }
}
