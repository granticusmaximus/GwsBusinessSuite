using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPageReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WikiBlockId",
                table: "SentinelNotifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PageReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WikiBlockId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUsername = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    DueUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    SentUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelledUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageReminders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageReminders_SentUnix_CancelledUnix_DueUnix",
                table: "PageReminders",
                columns: new[] { "SentUnix", "CancelledUnix", "DueUnix" });

            migrationBuilder.CreateIndex(
                name: "IX_PageReminders_WikiPageId_WikiBlockId",
                table: "PageReminders",
                columns: new[] { "WikiPageId", "WikiBlockId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageReminders");

            migrationBuilder.DropColumn(
                name: "WikiBlockId",
                table: "SentinelNotifications");
        }
    }
}
