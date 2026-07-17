using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsItemUnixTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NewsItems_TopicId_FetchedAt",
                table: "NewsItems");

            migrationBuilder.AddColumn<long>(
                name: "FetchedAtUnixSeconds",
                table: "NewsItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PublishedAtUnixSeconds",
                table: "NewsItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "NewsItems"
                SET "FetchedAtUnixSeconds" = CAST(strftime('%s', "FetchedAt") AS INTEGER),
                    "PublishedAtUnixSeconds" = CASE
                        WHEN "PublishedAt" IS NULL THEN NULL
                        ELSE CAST(strftime('%s', "PublishedAt") AS INTEGER)
                    END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_PublishedAtUnixSeconds",
                table: "NewsItems",
                column: "PublishedAtUnixSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_TopicId_FetchedAtUnixSeconds",
                table: "NewsItems",
                columns: new[] { "TopicId", "FetchedAtUnixSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NewsItems_PublishedAtUnixSeconds",
                table: "NewsItems");

            migrationBuilder.DropIndex(
                name: "IX_NewsItems_TopicId_FetchedAtUnixSeconds",
                table: "NewsItems");

            migrationBuilder.DropColumn(
                name: "FetchedAtUnixSeconds",
                table: "NewsItems");

            migrationBuilder.DropColumn(
                name: "PublishedAtUnixSeconds",
                table: "NewsItems");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_TopicId_FetchedAt",
                table: "NewsItems",
                columns: new[] { "TopicId", "FetchedAt" });
        }
    }
}
