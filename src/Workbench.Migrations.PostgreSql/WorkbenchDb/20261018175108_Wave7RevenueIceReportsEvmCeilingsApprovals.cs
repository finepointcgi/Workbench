using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegatorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DelegateEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalWorkflow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalWorkflow", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnitApprover",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false)
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
                name: "RevenueDelivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    RecordedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueDelivery_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueDelivery_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueEstimate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedRevenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueEstimate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueEstimate_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueEstimate_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PostedKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true, collation: "C"),
                    RecognizedToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PeriodRevenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BilledToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RevenueAdjustment = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnbilledBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BillingsInExcessBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LossProvisionBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReversedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueRun_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueRun_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueRun_GlJournalEntries_ReversalEntryId",
                        column: x => x.ReversalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueRun_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueSetup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FeeRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    OdcMarkupRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    TransactionPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueSetup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueSetup_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueSetup_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    SharedWith = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    StandardKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    DefinitionJson = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalWorkflowVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
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
                name: "RevenueRunLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CostToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    EstimatedRevenue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PercentComplete = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    RecognizedToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BilledToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpectedLoss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LossProvision = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueRunLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueRunLine_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RevenueRunLine_RevenueRun_RunId",
                        column: x => x.RunId,
                        principalTable: "RevenueRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: true),
                    HourUtc = table.Column<int>(type: "integer", nullable: false),
                    Delivery = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    AdditionalRecipients = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmitterEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    CurrentSequence = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    OnBehalfOf = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    StepName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ScopeKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, collation: "C"),
                    ScopeLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AssigneesCsv = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    AssigneeRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    ExcludedCsv = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    AdminsMayAct = table.Column<bool>(type: "boolean", nullable: false),
                    RemindAfterBusinessDays = table.Column<int>(type: "integer", nullable: true),
                    EscalateToSupervisor = table.Column<bool>(type: "boolean", nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    OnBehalfOf = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    SkipReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    LastRemindedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemindersSent = table.Column<int>(type: "integer", nullable: false)
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
                name: "IX_RevenueDelivery_ClinId",
                table: "RevenueDelivery",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueDelivery_ProjectId_DeliveredOn",
                table: "RevenueDelivery",
                columns: new[] { "ProjectId", "DeliveredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_RevenueEstimate_ClinId",
                table: "RevenueEstimate",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueEstimate_ProjectId_ClinId_AsOf",
                table: "RevenueEstimate",
                columns: new[] { "ProjectId", "ClinId", "AsOf" });

            migrationBuilder.CreateIndex(
                name: "IX_RevenueEstimate_Status",
                table: "RevenueEstimate",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRun_FiscalPeriodId",
                table: "RevenueRun",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRun_JournalEntryId",
                table: "RevenueRun",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRun_PostedKey",
                table: "RevenueRun",
                column: "PostedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRun_ProjectId_FiscalPeriodId",
                table: "RevenueRun",
                columns: new[] { "ProjectId", "FiscalPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRun_ReversalEntryId",
                table: "RevenueRun",
                column: "ReversalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRunLine_ClinId",
                table: "RevenueRunLine",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueRunLine_RunId",
                table: "RevenueRunLine",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueSetup_ClinId",
                table: "RevenueSetup",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueSetup_ProjectId_ClinId",
                table: "RevenueSetup",
                columns: new[] { "ProjectId", "ClinId" },
                unique: true);

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
                name: "RevenueDelivery");

            migrationBuilder.DropTable(
                name: "RevenueEstimate");

            migrationBuilder.DropTable(
                name: "RevenueRunLine");

            migrationBuilder.DropTable(
                name: "RevenueSetup");

            migrationBuilder.DropTable(
                name: "ApprovalInstance");

            migrationBuilder.DropTable(
                name: "SavedReports");

            migrationBuilder.DropTable(
                name: "RevenueRun");

            migrationBuilder.DropTable(
                name: "ApprovalWorkflowVersion");

            migrationBuilder.DropTable(
                name: "ApprovalWorkflow");
        }
    }
}
