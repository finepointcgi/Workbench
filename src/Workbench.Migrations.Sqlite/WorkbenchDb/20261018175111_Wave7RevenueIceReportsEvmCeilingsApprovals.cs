using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
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
                name: "Ceiling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LaborCategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HoursCeiling = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: true),
                    CostCeiling = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    BillingCeiling = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Enforcement = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    AlertThresholds = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectManagerMayOverride = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ceiling", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ceiling_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ceiling_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ceiling_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ceiling_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ceiling_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Closeouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    PhysicallyCompleteOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FinalInvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeobligationAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    DeobligationRecordedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DeobligationRecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleaseOfClaimsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ReleaseOfClaimsNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AssignmentOfRefundsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AssignmentOfRefundsNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RecordsRetentionUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    StatusBeforeClose = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReopenCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Closeouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Closeouts_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Closeouts_Invoice_FinalInvoiceId",
                        column: x => x.FinalInvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Closeouts_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmBaseline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BudgetVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleBaselineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ControlAccountLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagementReserve = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    UndistributedBudget = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ThresholdPercent = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    ThresholdAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmBaseline", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmBaseline_BudgetVersion_BudgetVersionId",
                        column: x => x.BudgetVersionId,
                        principalTable: "BudgetVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmBaseline_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmStatusPeriod",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    BaselineRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagementReserve = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    UndistributedBudget = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ReopenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReopenedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReopenReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmStatusPeriod", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmStatusPeriod_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmStatusPeriod_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmVarianceAnalysis",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ControlAccountWbsId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cause = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Impact = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CorrectiveAction = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ManagerEac = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmVarianceAnalysis", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmVarianceAnalysis_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmVarianceAnalysis_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmVarianceAnalysis_WbsElement_ControlAccountWbsId",
                        column: x => x.ControlAccountWbsId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncurredCostSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalYear = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    SchedulesJson = table.Column<string>(type: "TEXT", maxLength: 5000000, nullable: false),
                    LastDiffJson = table.Column<string>(type: "TEXT", maxLength: 200000, nullable: true),
                    GeneratedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertifiedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    CertifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertifierName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CertifierTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CertificateText = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmissionReference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncurredCostSubmissions", x => x.Id);
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
                name: "RateCeiling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FiscalYear = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxRate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 6, nullable: true),
                    MaxAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateCeiling", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RateCeiling_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RateCeiling_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueDelivery",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeliveredOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RecordedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AsOf = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    EstimatedRevenue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PostedKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    RecognizedToDate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    PeriodRevenue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    BilledToDate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    RevenueAdjustment = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    UnbilledBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    BillingsInExcessBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LossProvisionBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReversalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PostedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReversedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReversalReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Method = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FeeRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    OdcMarkupRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    TransactionPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                name: "CeilingAlert",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CeilingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Measure = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    Limit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Consumed = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CeilingAlert", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CeilingAlert_Ceiling_CeilingId",
                        column: x => x.CeilingId,
                        principalTable: "Ceiling",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CeilingOverride",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CeilingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtraHours = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: true),
                    ExtraCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ExtraBilling = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokeReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CeilingOverride", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CeilingOverride_Ceiling_CeilingId",
                        column: x => x.CeilingId,
                        principalTable: "Ceiling",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CloseoutWaiver",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloseoutId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Item = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    WaivedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    WaivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloseoutWaiver", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CloseoutWaiver_Closeouts_CloseoutId",
                        column: x => x.CloseoutId,
                        principalTable: "Closeouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmChangeRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaselineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Justification = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AppliedRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmChangeRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmChangeRequest_EvmBaseline_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "EvmBaseline",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmChangeRequest_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmWorkPackage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaselineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ControlAccountWbsId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Technique = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Budget = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    PlannedStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PlannedFinish = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ApportionedToId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PercentCompleteOverride = table.Column<decimal>(type: "TEXT", precision: 7, scale: 2, nullable: true),
                    ProgressUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProgressUpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmWorkPackage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmWorkPackage_EvmBaseline_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "EvmBaseline",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmWorkPackage_EvmWorkPackage_ApportionedToId",
                        column: x => x.ApportionedToId,
                        principalTable: "EvmWorkPackage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmWorkPackage_WbsElement_ControlAccountWbsId",
                        column: x => x.ControlAccountWbsId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvmWorkPackage_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmPeriodSnapshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatusPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkPackageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ControlAccountWbsId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Technique = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Budget = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Bcws = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Bcwp = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Acwp = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    PercentComplete = table.Column<decimal>(type: "TEXT", precision: 7, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmPeriodSnapshot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmPeriodSnapshot_EvmStatusPeriod_StatusPeriodId",
                        column: x => x.StatusPeriodId,
                        principalTable: "EvmStatusPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvmPeriodSnapshot_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncurredCostInputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RowKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Column = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncurredCostInputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncurredCostInputs_IncurredCostSubmissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "IncurredCostSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RevenueRunLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Method = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CostToDate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    EstimatedRevenue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    PercentComplete = table.Column<decimal>(type: "TEXT", precision: 9, scale: 6, nullable: true),
                    RecognizedToDate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    BilledToDate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ExpectedLoss = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LossProvision = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
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
                name: "EvmChangeRequestLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    BudgetChange = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    NewTechnique = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    NewStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    NewFinish = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmChangeRequestLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmChangeRequestLine_EvmChangeRequest_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "EvmChangeRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvmChangeRequestLine_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvmMilestone",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkPackageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Weight = table.Column<decimal>(type: "TEXT", precision: 7, scale: 2, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlannedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CompletedOn = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvmMilestone", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvmMilestone_EvmWorkPackage_WorkPackageId",
                        column: x => x.WorkPackageId,
                        principalTable: "EvmWorkPackage",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "IX_Ceiling_ClinId",
                table: "Ceiling",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_Ceiling_ContractId",
                table: "Ceiling",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Ceiling_LaborCategoryId",
                table: "Ceiling",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Ceiling_ProjectId",
                table: "Ceiling",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Ceiling_WbsElementId",
                table: "Ceiling",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_CeilingAlert_CeilingId_Measure_ThresholdPercent_Limit",
                table: "CeilingAlert",
                columns: new[] { "CeilingId", "Measure", "ThresholdPercent", "Limit" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CeilingOverride_CeilingId",
                table: "CeilingOverride",
                column: "CeilingId");

            migrationBuilder.CreateIndex(
                name: "IX_Closeouts_ContractId",
                table: "Closeouts",
                column: "ContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Closeouts_FinalInvoiceId",
                table: "Closeouts",
                column: "FinalInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Closeouts_ProjectId",
                table: "Closeouts",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CloseoutWaiver_CloseoutId_Item",
                table: "CloseoutWaiver",
                columns: new[] { "CloseoutId", "Item" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvmBaseline_BudgetVersionId",
                table: "EvmBaseline",
                column: "BudgetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmBaseline_ProjectId",
                table: "EvmBaseline",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvmChangeRequest_BaselineId",
                table: "EvmChangeRequest",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmChangeRequest_ProjectId_Number",
                table: "EvmChangeRequest",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvmChangeRequestLine_ChangeRequestId",
                table: "EvmChangeRequestLine",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmChangeRequestLine_WbsElementId",
                table: "EvmChangeRequestLine",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmMilestone_WorkPackageId_Order",
                table: "EvmMilestone",
                columns: new[] { "WorkPackageId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_EvmPeriodSnapshot_StatusPeriodId",
                table: "EvmPeriodSnapshot",
                column: "StatusPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmPeriodSnapshot_WbsElementId",
                table: "EvmPeriodSnapshot",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmStatusPeriod_FiscalPeriodId",
                table: "EvmStatusPeriod",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmStatusPeriod_ProjectId_PeriodStart",
                table: "EvmStatusPeriod",
                columns: new[] { "ProjectId", "PeriodStart" });

            migrationBuilder.CreateIndex(
                name: "IX_EvmVarianceAnalysis_ControlAccountWbsId",
                table: "EvmVarianceAnalysis",
                column: "ControlAccountWbsId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmVarianceAnalysis_FiscalPeriodId",
                table: "EvmVarianceAnalysis",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmVarianceAnalysis_ProjectId_FiscalPeriodId_ControlAccountWbsId",
                table: "EvmVarianceAnalysis",
                columns: new[] { "ProjectId", "FiscalPeriodId", "ControlAccountWbsId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvmWorkPackage_ApportionedToId",
                table: "EvmWorkPackage",
                column: "ApportionedToId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmWorkPackage_BaselineId_WbsElementId",
                table: "EvmWorkPackage",
                columns: new[] { "BaselineId", "WbsElementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvmWorkPackage_ControlAccountWbsId",
                table: "EvmWorkPackage",
                column: "ControlAccountWbsId");

            migrationBuilder.CreateIndex(
                name: "IX_EvmWorkPackage_WbsElementId",
                table: "EvmWorkPackage",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_IncurredCostInputs_SubmissionId_Schedule_RowKey_Column",
                table: "IncurredCostInputs",
                columns: new[] { "SubmissionId", "Schedule", "RowKey", "Column" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncurredCostSubmissions_FiscalYear",
                table: "IncurredCostSubmissions",
                column: "FiscalYear",
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
                name: "IX_RateCeiling_ClinId",
                table: "RateCeiling",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_RateCeiling_ContractId",
                table: "RateCeiling",
                column: "ContractId");

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
                name: "CeilingAlert");

            migrationBuilder.DropTable(
                name: "CeilingOverride");

            migrationBuilder.DropTable(
                name: "CloseoutWaiver");

            migrationBuilder.DropTable(
                name: "EvmChangeRequestLine");

            migrationBuilder.DropTable(
                name: "EvmMilestone");

            migrationBuilder.DropTable(
                name: "EvmPeriodSnapshot");

            migrationBuilder.DropTable(
                name: "EvmVarianceAnalysis");

            migrationBuilder.DropTable(
                name: "IncurredCostInputs");

            migrationBuilder.DropTable(
                name: "OrgUnitApprover");

            migrationBuilder.DropTable(
                name: "RateCeiling");

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
                name: "Ceiling");

            migrationBuilder.DropTable(
                name: "Closeouts");

            migrationBuilder.DropTable(
                name: "EvmChangeRequest");

            migrationBuilder.DropTable(
                name: "EvmWorkPackage");

            migrationBuilder.DropTable(
                name: "EvmStatusPeriod");

            migrationBuilder.DropTable(
                name: "IncurredCostSubmissions");

            migrationBuilder.DropTable(
                name: "SavedReports");

            migrationBuilder.DropTable(
                name: "RevenueRun");

            migrationBuilder.DropTable(
                name: "ApprovalWorkflowVersion");

            migrationBuilder.DropTable(
                name: "EvmBaseline");

            migrationBuilder.DropTable(
                name: "ApprovalWorkflow");
        }
    }
}
