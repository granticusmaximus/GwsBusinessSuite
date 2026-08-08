using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelPresenceLeaseUnixSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentinelPresenceLeases_WikiPageId_LastSeenAt",
                table: "SentinelPresenceLeases");

            migrationBuilder.AddColumn<long>(
                name: "LastSeenAtUnixSeconds",
                table: "SentinelPresenceLeases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPresenceLeases_LastSeenAtUnixSeconds",
                table: "SentinelPresenceLeases",
                column: "LastSeenAtUnixSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPresenceLeases_WikiPageId_LastSeenAtUnixSeconds",
                table: "SentinelPresenceLeases",
                columns: new[] { "WikiPageId", "LastSeenAtUnixSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentinelPresenceLeases_LastSeenAtUnixSeconds",
                table: "SentinelPresenceLeases");

            migrationBuilder.DropIndex(
                name: "IX_SentinelPresenceLeases_WikiPageId_LastSeenAtUnixSeconds",
                table: "SentinelPresenceLeases");

            migrationBuilder.DropColumn(
                name: "LastSeenAtUnixSeconds",
                table: "SentinelPresenceLeases");

            migrationBuilder.CreateIndex(
                name: "IX_SentinelPresenceLeases_WikiPageId_LastSeenAt",
                table: "SentinelPresenceLeases",
                columns: new[] { "WikiPageId", "LastSeenAt" });
        }
    }
}
