using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaWatchReadingDesk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                table: "SavedNewsArticles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReadingMinutes",
                table: "SavedNewsArticles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastReadingDigestAt",
                table: "NewsWatchSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MutedKeywords",
                table: "NewsWatchSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MutedSources",
                table: "NewsWatchSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CommunityScore",
                table: "NewsItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReadingMinutes",
                table: "NewsItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "SavedNewsArticles");

            migrationBuilder.DropColumn(
                name: "ReadingMinutes",
                table: "SavedNewsArticles");

            migrationBuilder.DropColumn(
                name: "LastReadingDigestAt",
                table: "NewsWatchSettings");

            migrationBuilder.DropColumn(
                name: "MutedKeywords",
                table: "NewsWatchSettings");

            migrationBuilder.DropColumn(
                name: "MutedSources",
                table: "NewsWatchSettings");

            migrationBuilder.DropColumn(
                name: "CommunityScore",
                table: "NewsItems");

            migrationBuilder.DropColumn(
                name: "ReadingMinutes",
                table: "NewsItems");
        }
    }
}
