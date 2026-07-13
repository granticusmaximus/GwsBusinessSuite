using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeoArticleDraftRevisionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeoArticleDraftRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeoArticleDraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ArticleMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                    OutlineMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                    ChangeType = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoArticleDraftRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeoArticleDraftRevisions_SeoArticleDrafts_SeoArticleDraftId",
                        column: x => x.SeoArticleDraftId,
                        principalTable: "SeoArticleDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeoArticleDraftRevisions_SeoArticleDraftId_VersionNumber",
                table: "SeoArticleDraftRevisions",
                columns: new[] { "SeoArticleDraftId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeoArticleDraftRevisions");
        }
    }
}
