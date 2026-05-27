using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeoArticleAffiliatePlacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeoArticleAffiliatePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeoArticleDraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SlotToken = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserId = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    TrackingUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CallToActionText = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoArticleAffiliatePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeoArticleAffiliatePlacements_SeoArticleDrafts_SeoArticleDraftId",
                        column: x => x.SeoArticleDraftId,
                        principalTable: "SeoArticleDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeoArticleAffiliatePlacements_SeoArticleDraftId_SortOrder",
                table: "SeoArticleAffiliatePlacements",
                columns: new[] { "SeoArticleDraftId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeoArticleAffiliatePlacements");
        }
    }
}
