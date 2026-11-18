using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <remarks>
    /// The tables of the government contracting module (CPSR files, CAS disclosure, CMMC, incurred cost) were taken out of this migration:
    /// the module creates them (docs/operations/module-extraction-govcon.md). Databases that already ran it are unaffected.
    /// </remarks>
    public partial class Wave7RevenueIceReportsEvmCeilingsApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalDelegation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DelegatorEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DelegateEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DocumentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalWorkflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalWorkflow", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnitApprover",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnitApprover", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgUnitApprover_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrgUnitApprover_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedWith = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StandardKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DefinitionJson = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalWorkflowVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalWorkflowVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalWorkflowVersion_ApprovalWorkflow_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "ApprovalWorkflow",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SavedReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Frequency = table.Column<int>(type: "INTEGER", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: true),
                    DayOfMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    HourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    Delivery = table.Column<int>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    AdditionalRecipients = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportSchedules_SavedReports_SavedReportId",
                        column: x => x.SavedReportId,
                        principalTable: "SavedReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalInstance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SubmitterEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Link = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    CurrentSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalInstance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalInstance_ApprovalWorkflowVersion_WorkflowVersionId",
                        column: x => x.WorkflowVersionId,
                        principalTable: "ApprovalWorkflowVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalAction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    OnBehalfOf = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalAction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalAction_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTask",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StepIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    StepName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ScopeLabel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AssigneesCsv = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    AssigneeRole = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ExcludedCsv = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AdminsMayAct = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemindAfterBusinessDays = table.Column<int>(type: "INTEGER", nullable: true),
                    EscalateToSupervisor = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    OnBehalfOf = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SkipReason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LastRemindedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RemindersSent = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalTask_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAction_InstanceId",
                table: "ApprovalAction",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegation_DelegateEmail",
                table: "ApprovalDelegation",
                column: "DelegateEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegation_DelegatorEmail",
                table: "ApprovalDelegation",
                column: "DelegatorEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalInstance_DocumentType_DocumentId",
                table: "ApprovalInstance",
                columns: new[] { "DocumentType", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalInstance_Status_DocumentType",
                table: "ApprovalInstance",
                columns: new[] { "Status", "DocumentType" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalInstance_WorkflowVersionId",
                table: "ApprovalInstance",
                column: "WorkflowVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_InstanceId_Sequence",
                table: "ApprovalTask",
                columns: new[] { "InstanceId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTask_Status",
                table: "ApprovalTask",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalWorkflow_DocumentType",
                table: "ApprovalWorkflow",
                column: "DocumentType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalWorkflowVersion_WorkflowId_Version",
                table: "ApprovalWorkflowVersion",
                columns: new[] { "WorkflowId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnitApprover_EmployeeId",
                table: "OrgUnitApprover",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnitApprover_OrgUnitId",
                table: "OrgUnitApprover",
                column: "OrgUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_Enabled_NextRunUtc",
                table: "ReportSchedules",
                columns: new[] { "Enabled", "NextRunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSchedules_SavedReportId",
                table: "ReportSchedules",
                column: "SavedReportId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedReports_OwnerEmail",
                table: "SavedReports",
                column: "OwnerEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalAction");

            migrationBuilder.DropTable(
                name: "ApprovalDelegation");

            migrationBuilder.DropTable(
                name: "ApprovalTask");

            migrationBuilder.DropTable(
                name: "OrgUnitApprover");

            migrationBuilder.DropTable(
                name: "ReportSchedules");

            migrationBuilder.DropTable(
                name: "ApprovalInstance");

            migrationBuilder.DropTable(
                name: "SavedReports");

            migrationBuilder.DropTable(
                name: "ApprovalWorkflowVersion");

            migrationBuilder.DropTable(
                name: "ApprovalWorkflow");
        }
    }
}
