using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBiReportSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BiReportSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    HourLocal = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiReportSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BiReportSubscriptions_OwnerUsername",
                table: "BiReportSubscriptions",
                column: "OwnerUsername",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BiReportSubscriptions");
        }
    }
}
