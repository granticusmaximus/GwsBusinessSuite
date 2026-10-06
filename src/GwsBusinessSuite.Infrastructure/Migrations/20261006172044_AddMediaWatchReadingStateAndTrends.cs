using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaWatchReadingStateAndTrends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastRefreshIssue",
                table: "WatchedTopics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerNewsArticlesFound",
                table: "AutomationWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "NewsReadMarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    UrlHash = table.Column<string>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsReadMarks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsTrendDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TopicKey = table.Column<string>(type: "TEXT", nullable: false),
                    Day = table.Column<string>(type: "TEXT", nullable: false),
                    ArticleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TermCountsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsTrendDays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsWatchSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RetentionHours = table.Column<int>(type: "INTEGER", nullable: false),
                    DigestEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DigestRecipient = table.Column<string>(type: "TEXT", nullable: true),
                    DigestFrequency = table.Column<string>(type: "TEXT", nullable: false),
                    DigestHourLocal = table.Column<int>(type: "INTEGER", nullable: false),
                    DigestDayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDigestSentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsWatchSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedNewsArticles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    UrlHash = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    TopicName = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    ClippedWikiPageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedNewsArticles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsReadMarks_Username_UrlHash",
                table: "NewsReadMarks",
                columns: new[] { "Username", "UrlHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsTrendDays_TopicKey_Day",
                table: "NewsTrendDays",
                columns: new[] { "TopicKey", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedNewsArticles_Username_UrlHash",
                table: "SavedNewsArticles",
                columns: new[] { "Username", "UrlHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsReadMarks");

            migrationBuilder.DropTable(
                name: "NewsTrendDays");

            migrationBuilder.DropTable(
                name: "NewsWatchSettings");

            migrationBuilder.DropTable(
                name: "SavedNewsArticles");

            migrationBuilder.DropColumn(
                name: "LastRefreshIssue",
                table: "WatchedTopics");

            migrationBuilder.DropColumn(
                name: "TriggerNewsArticlesFound",
                table: "AutomationWorkflows");
        }
    }
}
