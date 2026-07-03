using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCmsPageDraftPublishStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "CmsPages",
                type: "TEXT",
                nullable: true);

            // Every existing page is already live in production — defaulting new rows to
            // "Draft" would take the whole site down the moment this migration runs. Only
            // pages created after this ships start as Draft (see CmsBuilderService.SavePageAsync).
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "CmsPages",
                type: "TEXT",
                nullable: false,
                defaultValue: "Published");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "CmsPages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "CmsPages");
        }
    }
}
