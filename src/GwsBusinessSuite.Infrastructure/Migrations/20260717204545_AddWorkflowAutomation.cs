using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GwsBusinessSuite.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TypeKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedData = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationWorkflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    TagsCsv = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastExecutedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    WebhookPath = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduleIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    NextScheduledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NextScheduledAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationWorkflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceOutput = table.Column<string>(type: "TEXT", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetInput = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationConnections_AutomationWorkflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "AutomationWorkflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    InputJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StartedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryOfExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationExecutions_AutomationWorkflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "AutomationWorkflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TypeKey = table.Column<string>(type: "TEXT", nullable: false),
                    TypeVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionX = table.Column<double>(type: "REAL", nullable: false),
                    PositionY = table.Column<double>(type: "REAL", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContinueOnFail = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryOnFail = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxTries = table.Column<int>(type: "INTEGER", nullable: false),
                    WaitBetweenTriesMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationNodes_AutomationWorkflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "AutomationWorkflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationWorkflowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    ChangeSummary = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationWorkflowVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationWorkflowVersions_AutomationWorkflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "AutomationWorkflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationNodeExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeName = table.Column<string>(type: "TEXT", nullable: false),
                    NodeTypeKey = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    InputJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinishedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationNodeExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationNodeExecutions_AutomationExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "AutomationExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationConnections_WorkflowId_SourceNodeId_SourceOutput_TargetNodeId_TargetInput",
                table: "AutomationConnections",
                columns: new[] { "WorkflowId", "SourceNodeId", "SourceOutput", "TargetNodeId", "TargetInput" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationCredentials_Name",
                table: "AutomationCredentials",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_Status",
                table: "AutomationExecutions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkflowId_StartedAtUnixSeconds",
                table: "AutomationExecutions",
                columns: new[] { "WorkflowId", "StartedAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationNodeExecutions_ExecutionId_StartedAtUnixSeconds",
                table: "AutomationNodeExecutions",
                columns: new[] { "ExecutionId", "StartedAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationNodes_WorkflowId_Name",
                table: "AutomationNodes",
                columns: new[] { "WorkflowId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationWorkflows_Name",
                table: "AutomationWorkflows",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationWorkflows_Status",
                table: "AutomationWorkflows",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationWorkflows_Status_NextScheduledAtUnixSeconds",
                table: "AutomationWorkflows",
                columns: new[] { "Status", "NextScheduledAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationWorkflows_WebhookPath",
                table: "AutomationWorkflows",
                column: "WebhookPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationWorkflowVersions_WorkflowId_VersionNumber",
                table: "AutomationWorkflowVersions",
                columns: new[] { "WorkflowId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationConnections");

            migrationBuilder.DropTable(
                name: "AutomationCredentials");

            migrationBuilder.DropTable(
                name: "AutomationNodeExecutions");

            migrationBuilder.DropTable(
                name: "AutomationNodes");

            migrationBuilder.DropTable(
                name: "AutomationWorkflowVersions");

            migrationBuilder.DropTable(
                name: "AutomationExecutions");

            migrationBuilder.DropTable(
                name: "AutomationWorkflows");
        }
    }
}
