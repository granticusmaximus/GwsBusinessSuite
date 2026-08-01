using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivacyOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivacyRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestNumber = table.Column<string>(type: "TEXT", nullable: false),
                    RequestType = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IdentityVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IdentityVerifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DecisionNotes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrivacyRetentionPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DataCategory = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    LegalBasis = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutomationApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyRetentionPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    BreachAwarenessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PersonalDataInvolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    EphiInvolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    RiskAssessment = table.Column<string>(type: "TEXT", nullable: false),
                    RegulatorNotificationRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegulatorNotificationDueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RegulatorNotifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ContainedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityIncidentUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecurityIncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdateType = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityIncidentUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityIncidentUpdates_SecurityIncidents_SecurityIncidentId",
                        column: x => x.SecurityIncidentId,
                        principalTable: "SecurityIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyRequests_RequestNumber",
                table: "PrivacyRequests",
                column: "RequestNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyRequests_Status_DueAt",
                table: "PrivacyRequests",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyRetentionPolicies_DataCategory",
                table: "PrivacyRetentionPolicies",
                column: "DataCategory",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityIncidents_IncidentNumber",
                table: "SecurityIncidents",
                column: "IncidentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityIncidents_Status_RegulatorNotificationDueAt",
                table: "SecurityIncidents",
                columns: new[] { "Status", "RegulatorNotificationDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityIncidentUpdates_SecurityIncidentId_CreatedAt",
                table: "SecurityIncidentUpdates",
                columns: new[] { "SecurityIncidentId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivacyRequests");

            migrationBuilder.DropTable(
                name: "PrivacyRetentionPolicies");

            migrationBuilder.DropTable(
                name: "SecurityIncidentUpdates");

            migrationBuilder.DropTable(
                name: "SecurityIncidents");
        }
    }
}
