using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ShowAllAdminNavAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2026-10-03: every admin area goes back in the menu (several were hidden by default
            // because they were empty). NULL now means "nothing hidden"; hiding stays opt-in in
            // Settings. One-time: later choices in Settings are untouched by this migration.
            migrationBuilder.Sql("UPDATE \"SiteSettings\" SET \"HiddenNavKeys\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
