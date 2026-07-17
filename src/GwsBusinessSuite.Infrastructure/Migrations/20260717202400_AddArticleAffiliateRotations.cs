using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArticleAffiliateRotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticArticleRotationEnabled",
                table: "CjConnectorSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Existing connected installations opt in to the requested rotation feature.
            // New rows still use the entity's true initializer; the database default only
            // exists because SQLite needs a value while adding a non-null column.
            migrationBuilder.Sql(
                "UPDATE CjConnectorSettings SET AutomaticArticleRotationEnabled = 1;");

            migrationBuilder.CreateTable(
                name: "ArticleAffiliateRotations",
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
                    CallToActionText = table.Column<string>(type: "TEXT", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartsAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAffiliateRotations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAffiliateRotations_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateRotations_AffiliateOfferId_StartsAtUnixSeconds",
                table: "ArticleAffiliateRotations",
                columns: new[] { "AffiliateOfferId", "StartsAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAffiliateRotations_ArticleId_EndedAtUnixSeconds_ExpiresAtUnixSeconds",
                table: "ArticleAffiliateRotations",
                columns: new[] { "ArticleId", "EndedAtUnixSeconds", "ExpiresAtUnixSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleAffiliateRotations");

            migrationBuilder.DropColumn(
                name: "AutomaticArticleRotationEnabled",
                table: "CjConnectorSettings");
        }
    }
}
