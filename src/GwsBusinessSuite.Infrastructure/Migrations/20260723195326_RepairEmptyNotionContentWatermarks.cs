using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepairEmptyNotionContentWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The first incremental-sync release bootstrapped item watermarks from the
            // connector's global LastSyncedAt, including page shells whose earlier content
            // import had failed. Clear only those empty-page watermarks so the next sync
            // performs a real block fetch; successfully imported pages remain incremental.
            migrationBuilder.Sql(
                """
                UPDATE WikiPages
                SET NotionLastEditedAt = NULL
                WHERE NotionId IS NOT NULL
                  AND NotionLastEditedAt IS NOT NULL
                  AND trim(BlocksJson) IN ('', '[]');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data repair is intentionally irreversible. Restoring the incorrect watermark
            // would make empty imported pages permanently eligible for incremental skipping.
        }
    }
}
