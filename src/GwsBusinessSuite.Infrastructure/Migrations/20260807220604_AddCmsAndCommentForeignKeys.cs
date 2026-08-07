using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCmsAndCommentForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These 5 relationships never had a real FK, so nothing has ever stopped an
            // orphaned row from existing (e.g. the admin article-delete endpoint removes an
            // Article without touching its Comments). Adding a strict FK to a live production
            // table with unknown historical data quality risks the migration itself failing on
            // deploy if any orphan already exists - this app applies pending migrations
            // automatically on startup, so a failed migration here would mean the app fails to
            // start, not just a caught error. Cleaning up pre-existing orphans first (deleting
            // rows that already point at nothing) guarantees the FK constraints below can
            // always be added successfully, regardless of the live data's current state.
            // CmsPages before CmsPageRevisions, so an orphaned page's own now-orphaned
            // revisions get caught by the second delete too.
            migrationBuilder.Sql(
                """DELETE FROM "CmsPages" WHERE "SiteId" NOT IN (SELECT "Id" FROM "CmsSites");""");
            migrationBuilder.Sql(
                """DELETE FROM "CmsPageCategories" WHERE "SiteId" NOT IN (SELECT "Id" FROM "CmsSites");""");
            migrationBuilder.Sql(
                """DELETE FROM "GlobalBlocks" WHERE "SiteId" NOT IN (SELECT "Id" FROM "CmsSites");""");
            migrationBuilder.Sql(
                """DELETE FROM "CmsPageRevisions" WHERE "PageId" NOT IN (SELECT "Id" FROM "CmsPages");""");
            migrationBuilder.Sql(
                """DELETE FROM "Comments" WHERE "ArticleId" NOT IN (SELECT "Id" FROM "Articles");""");

            migrationBuilder.AddForeignKey(
                name: "FK_CmsPageCategories_CmsSites_SiteId",
                table: "CmsPageCategories",
                column: "SiteId",
                principalTable: "CmsSites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CmsPageRevisions_CmsPages_PageId",
                table: "CmsPageRevisions",
                column: "PageId",
                principalTable: "CmsPages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CmsPages_CmsSites_SiteId",
                table: "CmsPages",
                column: "SiteId",
                principalTable: "CmsSites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_Articles_ArticleId",
                table: "Comments",
                column: "ArticleId",
                principalTable: "Articles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_GlobalBlocks_CmsSites_SiteId",
                table: "GlobalBlocks",
                column: "SiteId",
                principalTable: "CmsSites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CmsPageCategories_CmsSites_SiteId",
                table: "CmsPageCategories");

            migrationBuilder.DropForeignKey(
                name: "FK_CmsPageRevisions_CmsPages_PageId",
                table: "CmsPageRevisions");

            migrationBuilder.DropForeignKey(
                name: "FK_CmsPages_CmsSites_SiteId",
                table: "CmsPages");

            migrationBuilder.DropForeignKey(
                name: "FK_Comments_Articles_ArticleId",
                table: "Comments");

            migrationBuilder.DropForeignKey(
                name: "FK_GlobalBlocks_CmsSites_SiteId",
                table: "GlobalBlocks");
        }
    }
}
