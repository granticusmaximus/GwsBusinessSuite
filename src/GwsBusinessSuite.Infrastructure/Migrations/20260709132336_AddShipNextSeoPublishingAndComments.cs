using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShipNextSeoPublishingAndComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentCommentId",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FaviconUrl",
                table: "CmsSites",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "CmsSites",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalUrl",
                table: "CmsPages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "CmsPages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "CmsPages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalUrl",
                table: "CmsPageRevisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "CmsPageRevisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "CmsPageRevisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CmsPageCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmsPageCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ParentCommentId",
                table: "Comments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_CmsPages_CategoryId",
                table: "CmsPages",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CmsPageCategories_SiteId_Slug",
                table: "CmsPageCategories",
                columns: new[] { "SiteId", "Slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CmsPages_CmsPageCategories_CategoryId",
                table: "CmsPages",
                column: "CategoryId",
                principalTable: "CmsPageCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_Comments_ParentCommentId",
                table: "Comments",
                column: "ParentCommentId",
                principalTable: "Comments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CmsPages_CmsPageCategories_CategoryId",
                table: "CmsPages");

            migrationBuilder.DropForeignKey(
                name: "FK_Comments_Comments_ParentCommentId",
                table: "Comments");

            migrationBuilder.DropTable(
                name: "CmsPageCategories");

            migrationBuilder.DropIndex(
                name: "IX_Comments_ParentCommentId",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_CmsPages_CategoryId",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "ParentCommentId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "FaviconUrl",
                table: "CmsSites");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "CmsSites");

            migrationBuilder.DropColumn(
                name: "CanonicalUrl",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "CanonicalUrl",
                table: "CmsPageRevisions");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "CmsPageRevisions");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "CmsPageRevisions");
        }
    }
}
