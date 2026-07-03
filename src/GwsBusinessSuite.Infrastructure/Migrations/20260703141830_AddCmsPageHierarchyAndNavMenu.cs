using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCmsPageHierarchyAndNavMenu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CmsPages_SiteId_Slug",
                table: "CmsPages");

            migrationBuilder.AddColumn<string>(
                name: "NavMenuJson",
                table: "CmsSites",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentPageId",
                table: "CmsPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CmsPages_SiteId_ParentPageId_Slug",
                table: "CmsPages",
                columns: new[] { "SiteId", "ParentPageId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CmsPages_SiteId_ParentPageId_Slug",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "NavMenuJson",
                table: "CmsSites");

            migrationBuilder.DropColumn(
                name: "ParentPageId",
                table: "CmsPages");

            migrationBuilder.CreateIndex(
                name: "IX_CmsPages_SiteId_Slug",
                table: "CmsPages",
                columns: new[] { "SiteId", "Slug" },
                unique: true);
        }
    }
}
