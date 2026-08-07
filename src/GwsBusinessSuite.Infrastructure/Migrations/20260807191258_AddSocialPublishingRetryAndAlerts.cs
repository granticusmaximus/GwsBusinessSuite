using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialPublishingRetryAndAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                table: "SocialPostTargets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "SocialPostTargets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SocialPostAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SocialPostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PostTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPostAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPostAlerts_IsRead",
                table: "SocialPostAlerts",
                column: "IsRead");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialPostAlerts");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "SocialPostTargets");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "SocialPostTargets");
        }
    }
}
