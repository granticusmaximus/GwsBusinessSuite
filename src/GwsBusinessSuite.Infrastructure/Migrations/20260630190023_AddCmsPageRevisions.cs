using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCmsPageRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CmsPageRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RevisionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    BlocksJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetaTitle = table.Column<string>(type: "TEXT", nullable: false),
                    MetaDescription = table.Column<string>(type: "TEXT", nullable: false),
                    OgImageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CustomCss = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmsPageRevisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CmsPageRevisions_PageId_RevisionNumber",
                table: "CmsPageRevisions",
                columns: new[] { "PageId", "RevisionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CmsPageRevisions");
        }
    }
}
