using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentinelQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Terms = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    NotFound = table.Column<bool>(type: "INTEGER", nullable: false),
                    Uncited = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TopScore = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcesJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnsweredBy = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    AskedUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelQuestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelQuestions_NotFound_AskedUnix",
                table: "SentinelQuestions",
                columns: new[] { "NotFound", "AskedUnix" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelQuestions_Username_AskedUnix",
                table: "SentinelQuestions",
                columns: new[] { "Username", "AskedUnix" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelQuestions");
        }
    }
}
