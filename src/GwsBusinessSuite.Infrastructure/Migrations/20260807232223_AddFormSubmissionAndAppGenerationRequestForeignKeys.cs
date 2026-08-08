using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFormSubmissionAndAppGenerationRequestForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Neither relationship has ever had a real FK, so nothing has stopped an orphaned
            // row from existing. This app applies pending migrations automatically on startup,
            // so a failed migration here would mean the app fails to start - clean up any
            // pre-existing orphans first so the FK constraints below can always be added.
            migrationBuilder.Sql(
                """DELETE FROM "AppGenerationRequests" WHERE "TargetSiteId" NOT IN (SELECT "Id" FROM "CmsSites");""");
            migrationBuilder.Sql(
                """DELETE FROM "FormSubmissions" WHERE "PageId" NOT IN (SELECT "Id" FROM "CmsPages");""");

            migrationBuilder.AddForeignKey(
                name: "FK_AppGenerationRequests_CmsSites_TargetSiteId",
                table: "AppGenerationRequests",
                column: "TargetSiteId",
                principalTable: "CmsSites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FormSubmissions_CmsPages_PageId",
                table: "FormSubmissions",
                column: "PageId",
                principalTable: "CmsPages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppGenerationRequests_CmsSites_TargetSiteId",
                table: "AppGenerationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_FormSubmissions_CmsPages_PageId",
                table: "FormSubmissions");
        }
    }
}
