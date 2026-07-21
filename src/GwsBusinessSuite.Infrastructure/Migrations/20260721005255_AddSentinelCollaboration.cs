using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelCollaboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentinelDiscussions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlockId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelDiscussions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentinelDiscussions_WikiPages_WikiPageId",
                        column: x => x.WikiPageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SentinelNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentinelDiscussionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SentinelDiscussionCommentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SentinelDiscussionComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentinelDiscussionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelDiscussionComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentinelDiscussionComments_SentinelDiscussionComments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "SentinelDiscussionComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SentinelDiscussionComments_SentinelDiscussions_SentinelDiscussionId",
                        column: x => x.SentinelDiscussionId,
                        principalTable: "SentinelDiscussions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SentinelDiscussionReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentinelDiscussionCommentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Emoji = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentinelDiscussionReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentinelDiscussionReactions_SentinelDiscussionComments_SentinelDiscussionCommentId",
                        column: x => x.SentinelDiscussionCommentId,
                        principalTable: "SentinelDiscussionComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelDiscussionComments_ParentCommentId",
                table: "SentinelDiscussionComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelDiscussionComments_SentinelDiscussionId_CreatedAt",
                table: "SentinelDiscussionComments",
                columns: new[] { "SentinelDiscussionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelDiscussionReactions_SentinelDiscussionCommentId_Username_Emoji",
                table: "SentinelDiscussionReactions",
                columns: new[] { "SentinelDiscussionCommentId", "Username", "Emoji" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelDiscussions_WikiPageId_ResolvedAt",
                table: "SentinelDiscussions",
                columns: new[] { "WikiPageId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentinelNotifications_Username_ReadAt_CreatedAt",
                table: "SentinelNotifications",
                columns: new[] { "Username", "ReadAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentinelDiscussionReactions");

            migrationBuilder.DropTable(
                name: "SentinelNotifications");

            migrationBuilder.DropTable(
                name: "SentinelDiscussionComments");

            migrationBuilder.DropTable(
                name: "SentinelDiscussions");
        }
    }
}
