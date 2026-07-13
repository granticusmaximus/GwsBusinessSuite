using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAffiliateClicksAndCjCommissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArticleAffiliateClicks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlacementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdvertiserId = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserName = table.Column<string>(type: "TEXT", nullable: false),
                    TrackingUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAffiliateClicks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAffiliateClicks_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CjCommissionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserId = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserName = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: false),
                    ActionStatus = table.Column<string>(type: "TEXT", nullable: false),
                    SaleAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    CommissionAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    EventDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PostingDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CjCommissionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateClicks_AdvertiserId_CreatedAt",
                table: "ArticleAffiliateClicks",
                columns: new[] { "AdvertiserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateClicks_ArticleId",
                table: "ArticleAffiliateClicks",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateClicks_PlacementId_CreatedAt",
                table: "ArticleAffiliateClicks",
                columns: new[] { "PlacementId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CjCommissionRecords_AdvertiserId",
                table: "CjCommissionRecords",
                column: "AdvertiserId");

            migrationBuilder.CreateIndex(
                name: "IX_CjCommissionRecords_ExternalId",
                table: "CjCommissionRecords",
                column: "ExternalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleAffiliateClicks");

            migrationBuilder.DropTable(
                name: "CjCommissionRecords");
        }
    }
}
