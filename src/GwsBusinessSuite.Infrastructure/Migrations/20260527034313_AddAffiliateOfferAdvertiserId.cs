using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAffiliateOfferAdvertiserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdvertiserId",
                table: "AffiliateOffers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE AffiliateOffers
                SET AdvertiserId = CASE
                    WHEN IFNULL(TRIM(LinkName), '') = '' THEN AdvertiserName
                    ELSE LinkName
                END
                WHERE IFNULL(TRIM(AdvertiserId), '') = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdvertiserId",
                table: "AffiliateOffers");
        }
    }
}
