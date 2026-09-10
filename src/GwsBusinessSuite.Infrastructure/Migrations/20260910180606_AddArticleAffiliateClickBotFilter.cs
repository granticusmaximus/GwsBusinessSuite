using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArticleAffiliateClickBotFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilterReason",
                table: "ArticleAffiliateClicks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PassedBotFilter",
                table: "ArticleAffiliateClicks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilterReason",
                table: "ArticleAffiliateClicks");

            migrationBuilder.DropColumn(
                name: "PassedBotFilter",
                table: "ArticleAffiliateClicks");
        }
    }
}
