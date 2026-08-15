using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SemanticSearchDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SemanticSearchDocuments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SemanticSearchDocuments_EmbeddingModel_Dimensions",
                table: "SemanticSearchDocuments",
                columns: new[] { "EmbeddingModel", "Dimensions" });

            migrationBuilder.CreateIndex(
                name: "IX_SemanticSearchDocuments_ParentId",
                table: "SemanticSearchDocuments",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_SemanticSearchDocuments_SourceType_SourceId",
                table: "SemanticSearchDocuments",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SemanticSearchDocuments");
        }
    }
}
