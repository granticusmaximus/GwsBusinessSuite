using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAffiliateInteractionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeoArticleAffiliateInteractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeoArticleDraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SlotToken = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserId = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoArticleAffiliateInteractions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeoArticleAffiliateInteractions_AdvertiserId_EventType",
                table: "SeoArticleAffiliateInteractions",
                columns: new[] { "AdvertiserId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_SeoArticleAffiliateInteractions_SeoArticleDraftId_SlotToken_CreatedAt",
                table: "SeoArticleAffiliateInteractions",
                columns: new[] { "SeoArticleDraftId", "SlotToken", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeoArticleAffiliateInteractions");
        }
    }
}
