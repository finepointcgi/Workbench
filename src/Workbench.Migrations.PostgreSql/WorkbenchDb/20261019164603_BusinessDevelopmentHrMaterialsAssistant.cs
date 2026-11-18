using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class BusinessDevelopmentHrMaterialsAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBusinessDevelopment",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHr",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMaterials",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPricing",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBusinessDevelopment",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHr",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMaterials",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPricing",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssistantInteraction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    PagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Prompt = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, collation: "C"),
                    Answer = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: true, collation: "C"),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OutcomeDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    ToolCallCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantInteraction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Connection = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    MonthlyTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    MonthlyTokenLimitPerUser = table.Column<long>(type: "bigint", nullable: true),
                    DailyQuestionLimitPerUser = table.Column<int>(type: "integer", nullable: true),
                    ExplainTimesheetFlags = table.Column<bool>(type: "boolean", nullable: false),
                    NoticeAcceptedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    NoticeAcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NoticeVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenefitDeductionExport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PayGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    Content = table.Column<string>(type: "character varying(1000000)", maxLength: 1000000, nullable: false, collation: "C"),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    EmployeeTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EmployerTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExportedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ExportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitDeductionExport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenefitDeductionExport_PayGroup_PayGroupId",
                        column: x => x.PayGroupId,
                        principalTable: "PayGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BenefitDeductionExport_PayPeriod_PayPeriodId",
                        column: x => x.PayPeriodId,
                        principalTable: "PayPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenefitDependent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Relationship = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitDependent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenefitDependent_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenefitPlan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Carrier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DeductionCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    IsPreTax = table.Column<bool>(type: "boolean", nullable: false),
                    ExpenseAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsHighDeductible = table.Column<bool>(type: "boolean", nullable: false),
                    FullTimeOnly = table.Column<bool>(type: "boolean", nullable: false),
                    AnnualElectionLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    EmployerMatchRate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    EmployerMatchCapPercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    EmployerMonthlyContribution = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitPlan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenefitPlan_GlAccounts_ExpenseAccountId",
                        column: x => x.ExpenseAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistTemplate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTemplate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmergencyContact",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Relationship = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, collation: "C"),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmergencyContact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmergencyContact_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeCredential",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    ClearanceLevel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeCredential", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeCredential_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeDocument",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    EmployeeVisible = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeDocument", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeDocument_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeDocument_StoredFile_FileId",
                        column: x => x.FileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeEeoRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Gender = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Race = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    JobCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Disability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsVeteran = table.Column<bool>(type: "boolean", nullable: false),
                    DisabledVeteran = table.Column<bool>(type: "boolean", nullable: false),
                    RecentlySeparatedVeteran = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveDutyWartimeVeteran = table.Column<bool>(type: "boolean", nullable: false),
                    ArmedForcesServiceMedalVeteran = table.Column<bool>(type: "boolean", nullable: false),
                    DischargeDate = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeEeoRecord", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeEeoRecord_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HrChangeRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrChangeRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HrChangeRequest_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifeEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WindowEndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReportedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ReportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifeEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LifeEvent_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeritCycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    BudgetPercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeritCycle", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenEnrollmentWindow",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    OpensOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ClosesOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CoverageStartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenEnrollmentWindow", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Position",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportsToPositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EeoJobCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsExempt = table.Column<bool>(type: "boolean", nullable: false),
                    EmploymentType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SalaryMin = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    SalaryMax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Headcount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Position", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Position_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Position_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Position_Position_ReportsToPositionId",
                        column: x => x.ReportsToPositionId,
                        principalTable: "Position",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectNarrative",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    CostVariance = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    ScheduleVariance = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    EacChanges = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    Accomplishments = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    UpcomingMilestones = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    Risks = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    DraftSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectNarrative", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectNarrative_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectNarrative_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimesheetFlagExplanation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleKey = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false, collation: "C"),
                    FactsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimesheetFlagExplanation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantToolCall",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InteractionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Arguments = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RecordsRead = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CalledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantToolCall", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantToolCall_AssistantInteraction_InteractionId",
                        column: x => x.InteractionId,
                        principalTable: "AssistantInteraction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenefitTier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Coverage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EmployeeMonthlyCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EmployerMonthlyCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitTier", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenefitTier_BenefitPlan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "BenefitPlan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistTemplateTask",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Owner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DueOffsetDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTemplateTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistTemplateTask_ChecklistTemplate_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ChecklistTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeChecklist",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AnchorDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TerminationReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LeavePayoutHours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    FinalTimesheetApproved = table.Column<bool>(type: "boolean", nullable: false),
                    AccessDeprovisionedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeChecklist", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeChecklist_ChecklistTemplate_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ChecklistTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeChecklist_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompensationChange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PayType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AnnualSalary = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    HourlyRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    PreviousAnnualized = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    PayRateId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompensationChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompensationChange_EmployeePayRate_PayRateId",
                        column: x => x.PayRateId,
                        principalTable: "EmployeePayRate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompensationChange_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompensationChange_MeritCycle_CycleId",
                        column: x => x.CycleId,
                        principalTable: "MeritCycle",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeHrProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmploymentType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    WorkState = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true, collation: "C"),
                    PreferredName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    PersonalEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    PersonalPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true, collation: "C"),
                    AddressLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    AddressLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    State = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true, collation: "C"),
                    PostalCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true, collation: "C"),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TerminationReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeHrProfile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeHrProfile_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeHrProfile_Position_PositionId",
                        column: x => x.PositionId,
                        principalTable: "Position",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenefitElection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TierId = table.Column<Guid>(type: "uuid", nullable: true),
                    Coverage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ContributionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    IsWaived = table.Column<bool>(type: "boolean", nullable: false),
                    CoverageStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CoverageEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    Source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WindowId = table.Column<Guid>(type: "uuid", nullable: true),
                    LifeEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ElectedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ElectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitElection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenefitElection_BenefitPlan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "BenefitPlan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BenefitElection_BenefitTier_TierId",
                        column: x => x.TierId,
                        principalTable: "BenefitTier",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BenefitElection_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BenefitElection_LifeEvent_LifeEventId",
                        column: x => x.LifeEventId,
                        principalTable: "LifeEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BenefitElection_OpenEnrollmentWindow_WindowId",
                        column: x => x.WindowId,
                        principalTable: "OpenEnrollmentWindow",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChecklistTask",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChecklistId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Owner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AssigneeEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChecklistTask_EmployeeChecklist_ChecklistId",
                        column: x => x.ChecklistId,
                        principalTable: "EmployeeChecklist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChecklistTask_FixedAsset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "FixedAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenefitElectionDependent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ElectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenefitElectionDependent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenefitElectionDependent_BenefitDependent_DependentId",
                        column: x => x.DependentId,
                        principalTable: "BenefitDependent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BenefitElectionDependent_BenefitElection_ElectionId",
                        column: x => x.ElectionId,
                        principalTable: "BenefitElection",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantInteraction_ConversationId",
                table: "AssistantInteraction",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantInteraction_StartedAtUtc",
                table: "AssistantInteraction",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantInteraction_UserEmail_StartedAtUtc",
                table: "AssistantInteraction",
                columns: new[] { "UserEmail", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantToolCall_InteractionId_Sequence",
                table: "AssistantToolCall",
                columns: new[] { "InteractionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_BenefitDeductionExport_PayGroupId_PayPeriodId",
                table: "BenefitDeductionExport",
                columns: new[] { "PayGroupId", "PayPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_BenefitDeductionExport_PayPeriodId",
                table: "BenefitDeductionExport",
                column: "PayPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitDependent_EmployeeId",
                table: "BenefitDependent",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElection_EmployeeId_PlanType",
                table: "BenefitElection",
                columns: new[] { "EmployeeId", "PlanType" });

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElection_LifeEventId",
                table: "BenefitElection",
                column: "LifeEventId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElection_PlanId",
                table: "BenefitElection",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElection_TierId",
                table: "BenefitElection",
                column: "TierId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElection_WindowId",
                table: "BenefitElection",
                column: "WindowId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElectionDependent_DependentId",
                table: "BenefitElectionDependent",
                column: "DependentId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitElectionDependent_ElectionId_DependentId",
                table: "BenefitElectionDependent",
                columns: new[] { "ElectionId", "DependentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenefitPlan_Code",
                table: "BenefitPlan",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenefitPlan_ExpenseAccountId",
                table: "BenefitPlan",
                column: "ExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BenefitTier_PlanId_Coverage",
                table: "BenefitTier",
                columns: new[] { "PlanId", "Coverage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTask_AssetId",
                table: "ChecklistTask",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTask_AssigneeEmail",
                table: "ChecklistTask",
                column: "AssigneeEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTask_ChecklistId",
                table: "ChecklistTask",
                column: "ChecklistId");

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTask_Status_DueDate",
                table: "ChecklistTask",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistTemplateTask_TemplateId",
                table: "ChecklistTemplateTask",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_CompensationChange_CycleId",
                table: "CompensationChange",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_CompensationChange_EmployeeId_Status",
                table: "CompensationChange",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CompensationChange_PayRateId",
                table: "CompensationChange",
                column: "PayRateId");

            migrationBuilder.CreateIndex(
                name: "IX_EmergencyContact_EmployeeId",
                table: "EmergencyContact",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeChecklist_EmployeeId_Kind",
                table: "EmployeeChecklist",
                columns: new[] { "EmployeeId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeChecklist_TemplateId",
                table: "EmployeeChecklist",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCredential_EmployeeId",
                table: "EmployeeCredential",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCredential_ExpiresOn",
                table: "EmployeeCredential",
                column: "ExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeDocument_EmployeeId",
                table: "EmployeeDocument",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeDocument_FileId",
                table: "EmployeeDocument",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeEeoRecord_EmployeeId",
                table: "EmployeeEeoRecord",
                column: "EmployeeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeHrProfile_EmployeeId",
                table: "EmployeeHrProfile",
                column: "EmployeeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeHrProfile_PositionId",
                table: "EmployeeHrProfile",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_HrChangeRequest_EmployeeId_Status",
                table: "HrChangeRequest",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LifeEvent_EmployeeId_Status",
                table: "LifeEvent",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Position_Code",
                table: "Position",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Position_LaborCategoryId",
                table: "Position",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Position_OrgUnitId",
                table: "Position",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Position_ReportsToPositionId",
                table: "Position",
                column: "ReportsToPositionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNarrative_FiscalPeriodId",
                table: "ProjectNarrative",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNarrative_ProjectId_FiscalPeriodId",
                table: "ProjectNarrative",
                columns: new[] { "ProjectId", "FiscalPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetFlagExplanation_TimesheetId_RuleKey_FactsHash",
                table: "TimesheetFlagExplanation",
                columns: new[] { "TimesheetId", "RuleKey", "FactsHash" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantSettings");

            migrationBuilder.DropTable(
                name: "AssistantToolCall");

            migrationBuilder.DropTable(
                name: "BenefitDeductionExport");

            migrationBuilder.DropTable(
                name: "BenefitElectionDependent");

            migrationBuilder.DropTable(
                name: "ChecklistTask");

            migrationBuilder.DropTable(
                name: "ChecklistTemplateTask");

            migrationBuilder.DropTable(
                name: "CompensationChange");

            migrationBuilder.DropTable(
                name: "EmergencyContact");

            migrationBuilder.DropTable(
                name: "EmployeeCredential");

            migrationBuilder.DropTable(
                name: "EmployeeDocument");

            migrationBuilder.DropTable(
                name: "EmployeeEeoRecord");

            migrationBuilder.DropTable(
                name: "EmployeeHrProfile");

            migrationBuilder.DropTable(
                name: "HrChangeRequest");

            migrationBuilder.DropTable(
                name: "ProjectNarrative");

            migrationBuilder.DropTable(
                name: "TimesheetFlagExplanation");

            migrationBuilder.DropTable(
                name: "AssistantInteraction");

            migrationBuilder.DropTable(
                name: "BenefitDependent");

            migrationBuilder.DropTable(
                name: "BenefitElection");

            migrationBuilder.DropTable(
                name: "EmployeeChecklist");

            migrationBuilder.DropTable(
                name: "MeritCycle");

            migrationBuilder.DropTable(
                name: "Position");

            migrationBuilder.DropTable(
                name: "BenefitTier");

            migrationBuilder.DropTable(
                name: "LifeEvent");

            migrationBuilder.DropTable(
                name: "OpenEnrollmentWindow");

            migrationBuilder.DropTable(
                name: "ChecklistTemplate");

            migrationBuilder.DropTable(
                name: "BenefitPlan");

            migrationBuilder.DropColumn(
                name: "IsBusinessDevelopment",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsHr",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsMaterials",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsPricing",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsBusinessDevelopment",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsHr",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsMaterials",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsPricing",
                table: "AppUser");
        }
    }
}
