using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCmsPageRegions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "CmsPages",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CmsPages_SiteId_Region",
                table: "CmsPages",
                columns: new[] { "SiteId", "Region" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CmsPages_SiteId_Region",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "CmsPages");
        }
    }
}
