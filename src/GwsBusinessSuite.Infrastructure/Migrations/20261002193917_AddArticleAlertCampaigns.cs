using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArticleAlertCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActivatedAt",
                table: "EmailCampaigns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArticleAlertSettingsJson",
                table: "EmailCampaigns",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "EmailCampaigns",
                type: "TEXT",
                nullable: false,
                defaultValue: "Sequence");

            migrationBuilder.CreateTable(
                name: "ArticleAnnouncements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArticleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArticleTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DueAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RecipientCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveredCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAnnouncements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAnnouncements_EmailCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "EmailCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailCampaignSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmationSentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UnsubscribedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    ConsentText = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailCampaignSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailCampaignSubscriptions_Contacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "Contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EmailCampaignSubscriptions_EmailCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "EmailCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArticleAnnouncementDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleAnnouncementDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArticleAnnouncementDeliveries_ArticleAnnouncements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "ArticleAnnouncements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArticleAnnouncementDeliveries_EmailCampaignSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "EmailCampaignSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailCampaigns_Kind",
                table: "EmailCampaigns",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAnnouncementDeliveries_AnnouncementId_SubscriptionId",
                table: "ArticleAnnouncementDeliveries",
                columns: new[] { "AnnouncementId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAnnouncementDeliveries_SubscriptionId",
                table: "ArticleAnnouncementDeliveries",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAnnouncements_CampaignId_ArticleId",
                table: "ArticleAnnouncements",
                columns: new[] { "CampaignId", "ArticleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArticleAnnouncements_Status_DueAtUnixSeconds",
                table: "ArticleAnnouncements",
                columns: new[] { "Status", "DueAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailCampaignSubscriptions_CampaignId_ContactId",
                table: "EmailCampaignSubscriptions",
                columns: new[] { "CampaignId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailCampaignSubscriptions_CampaignId_Status",
                table: "EmailCampaignSubscriptions",
                columns: new[] { "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailCampaignSubscriptions_ContactId",
                table: "EmailCampaignSubscriptions",
                column: "ContactId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArticleAnnouncementDeliveries");

            migrationBuilder.DropTable(
                name: "ArticleAnnouncements");

            migrationBuilder.DropTable(
                name: "EmailCampaignSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_EmailCampaigns_Kind",
                table: "EmailCampaigns");

            migrationBuilder.DropColumn(
                name: "ActivatedAt",
                table: "EmailCampaigns");

            migrationBuilder.DropColumn(
                name: "ArticleAlertSettingsJson",
                table: "EmailCampaigns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "EmailCampaigns");
        }
    }
}
