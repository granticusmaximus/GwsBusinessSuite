using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebPushSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebPushKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPushKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebPushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Auth = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    LastDeliveredAtUnix = table.Column<long>(type: "INTEGER", nullable: true),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebPushSubscriptions_Endpoint",
                table: "WebPushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPushSubscriptions_Username",
                table: "WebPushSubscriptions",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebPushKeys");

            migrationBuilder.DropTable(
                name: "WebPushSubscriptions");
        }
    }
}
