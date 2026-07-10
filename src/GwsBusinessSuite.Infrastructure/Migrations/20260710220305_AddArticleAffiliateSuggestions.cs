using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArticleAffiliateSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArticleAffiliateSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AffiliateOfferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdvertiserId = table.Column<string>(type: "TEXT", nullable: false),
                    AdvertiserName = table.Column<string>(type: "TEXT", nullable: false),
                    LinkName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    TrackingUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAffiliateSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAffiliateSuggestions_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateSuggestions_ArticleId_Status",
                table: "ArticleAffiliateSuggestions",
                columns: new[] { "ArticleId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleAffiliateSuggestions");
        }
    }
}
