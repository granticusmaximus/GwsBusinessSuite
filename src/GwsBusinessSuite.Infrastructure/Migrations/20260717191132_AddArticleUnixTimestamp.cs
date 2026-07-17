using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArticleUnixTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PublishedAtUnixSeconds",
                table: "Articles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Articles"
                SET "PublishedAtUnixSeconds" = CASE
                    WHEN "PublishedAt" IS NULL THEN NULL
                    ELSE CAST(strftime('%s', "PublishedAt") AS INTEGER)
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Articles_Status_PublishedAtUnixSeconds",
                table: "Articles",
                columns: new[] { "Status", "PublishedAtUnixSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Articles_Status_PublishedAtUnixSeconds",
                table: "Articles");

            migrationBuilder.DropColumn(
                name: "PublishedAtUnixSeconds",
                table: "Articles");
        }
    }
}
