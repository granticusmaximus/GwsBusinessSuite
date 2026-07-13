using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmPipelineAndActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FollowUpDate",
                table: "Contacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrashedAt",
                table: "Contacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContactActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactActivities_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Status",
                table: "Contacts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TrashedAt",
                table: "Contacts",
                column: "TrashedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ContactActivities_ContactId_CreatedAt",
                table: "ContactActivities",
                columns: new[] { "ContactId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactActivities");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_Status",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_TrashedAt",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "FollowUpDate",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "TrashedAt",
                table: "Contacts");
        }
    }
}
