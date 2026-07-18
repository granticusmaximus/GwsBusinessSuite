using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSponsoredCardLinkNameAndImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "SeoArticleAffiliatePlacements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkName",
                table: "SeoArticleAffiliatePlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "ArticleAffiliateSuggestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "ArticleAffiliateRotations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "ArticleAffiliatePlacements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkName",
                table: "ArticleAffiliatePlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "AffiliateOffers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "SeoArticleAffiliatePlacements");

            migrationBuilder.DropColumn(
                name: "LinkName",
                table: "SeoArticleAffiliatePlacements");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "ArticleAffiliateSuggestions");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "ArticleAffiliateRotations");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "ArticleAffiliatePlacements");

            migrationBuilder.DropColumn(
                name: "LinkName",
                table: "ArticleAffiliatePlacements");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "AffiliateOffers");
        }
    }
}
