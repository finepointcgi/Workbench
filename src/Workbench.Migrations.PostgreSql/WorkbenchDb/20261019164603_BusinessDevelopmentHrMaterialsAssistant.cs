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
                name: "CrmAccount",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Acronym = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Uei = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    CageCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true, collation: "C"),
                    Website = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Phone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmAccount", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmAccount_CrmAccount_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CrmAccount_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CrmSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BpParentWbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmSettings_WbsElement_BpParentWbsElementId",
                        column: x => x.BpParentWbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "GateDefinition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ToStage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    PassingScore = table.Column<int>(type: "integer", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateDefinition", x => x.Id);
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
                name: "MrpRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    HorizonDays = table.Column<int>(type: "integer", nullable: false),
                    PlannedOrderCount = table.Column<int>(type: "integer", nullable: false),
                    LateCount = table.Column<int>(type: "integer", nullable: false),
                    Messages = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    RunBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    RunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrpRun", x => x.Id);
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
                name: "SavedLeadSearch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    NaicsCodes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Agencies = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    SetAsides = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Keywords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    PostedWithinDays = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunNewLeads = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedLeadSearch", x => x.Id);
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
                name: "Warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouse", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkCenter",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    HoursPerDay = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCenter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkCenter_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "CrmContact",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Phone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmContact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmContact_CrmAccount_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Opportunity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    SolicitationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    SolicitationKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    AgencyAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgencyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    NaicsCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    SetAside = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ContractVehicle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ContractType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EstimatedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedAwardDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodOfPerformanceMonths = table.Column<int>(type: "integer", nullable: true),
                    RfiDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RfpReleaseOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ProposalDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PlaceOfPerformance = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    IncumbentAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    IncumbentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    CaptureManagerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ProposalManagerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StageChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Pwin = table.Column<int>(type: "integer", nullable: false),
                    Pgo = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    SamNoticeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    OutcomeReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OutcomeNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    DecidedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AwardedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AwardedTo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    BpWbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Opportunity_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunity_CrmAccount_AgencyAccountId",
                        column: x => x.AgencyAccountId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunity_CrmAccount_IncumbentAccountId",
                        column: x => x.IncumbentAccountId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunity_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunity_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunity_WbsElement_BpWbsElementId",
                        column: x => x.BpWbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GateCriterion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GateDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Guidance = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Weight = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateCriterion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GateCriterion_GateDefinition_GateDefinitionId",
                        column: x => x.GateDefinitionId,
                        principalTable: "GateDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "Bin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bin", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bin_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
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
                name: "CrmActivity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AssignedTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    IsDone = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmActivity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmActivity_CrmAccount_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CrmActivity_CrmContact_ContactId",
                        column: x => x.ContactId,
                        principalTable: "CrmContact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CrmActivity_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityCompetitor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsIncumbent = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityCompetitor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityCompetitor_CrmAccount_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunityCompetitor_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityGateReview",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    GateDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GateName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ToStage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ScorePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    PassingScore = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityGateReview", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityGateReview_GateDefinition_GateDefinitionId",
                        column: x => x.GateDefinitionId,
                        principalTable: "GateDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunityGateReview_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityLead",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    SolicitationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    SolicitationKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Agency = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Office = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    NaicsCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    SetAside = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    NoticeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    PostedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ResponseDueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PlaceOfPerformance = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    EstimatedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    SavedSearchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityLead", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityLead_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunityLead_SavedLeadSearch_SavedSearchId",
                        column: x => x.SavedSearchId,
                        principalTable: "SavedLeadSearch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityStageChange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    To = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityStageChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityStageChange_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityTeammate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorksharePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityTeammate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityTeammate_CrmAccount_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CrmAccount",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunityTeammate_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Proposal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Customer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpportunityName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ContractType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WonModelId = table.Column<Guid>(type: "uuid", nullable: true),
                    WonContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    WonProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WonBudgetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    HandedOffAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HandedOffBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proposal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Proposal_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Part",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Revision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    UnitOfMeasure = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Tracking = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CostMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StandardCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AverageCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    InventoryValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PreferredVendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: false),
                    SafetyStock = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MinimumOrderQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OrderMultiple = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DefaultWarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultBinId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Part", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Part_Bin_DefaultBinId",
                        column: x => x.DefaultBinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Part_Vendor_PreferredVendorId",
                        column: x => x.PreferredVendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Part_Warehouse_DefaultWarehouseId",
                        column: x => x.DefaultWarehouseId,
                        principalTable: "Warehouse",
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

            migrationBuilder.CreateTable(
                name: "OpportunityGateScore",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    CriterionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Weight = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityGateScore", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityGateScore_OpportunityGateReview_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "OpportunityGateReview",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceModel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsBase = table.Column<bool>(type: "boolean", nullable: false),
                    SourceModelId = table.Column<Guid>(type: "uuid", nullable: true),
                    RateBasis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FringeOverride = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    OverheadOverride = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    GaOverride = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    RatesAsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    DefaultRateMethod = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    DefaultEscalation = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    FeeMethod = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FeeRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    WgTechnicalRisk = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    WgManagementRisk = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    WgContractTypeRisk = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    WgCostEfficiency = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: true),
                    LockedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    LockNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    LockedSnapshot = table.Column<string>(type: "character varying(4000000)", maxLength: 4000000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceModel", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceModel_Proposal_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "Proposal",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillOfMaterial",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillOfMaterial", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillOfMaterial_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryBalance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    GovernmentReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryBalance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryBalance_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryBalance_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryBalance_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryBalance_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PartForecast",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartForecast", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartForecast_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlannedOrder",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    NeedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsLate = table.Column<bool>(type: "boolean", nullable: false),
                    Pegging = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequisitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirmedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    FirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedOrder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedOrder_MrpRun_RunId",
                        column: x => x.RunId,
                        principalTable: "MrpRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedOrder_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedOrder_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedOrder_PurchaseRequisition_RequisitionId",
                        column: x => x.RequisitionId,
                        principalTable: "PurchaseRequisition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectMaterialRequirement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityIssued = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    NeededBy = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMaterialRequirement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectMaterialRequirement_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectMaterialRequirement_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectMaterialRequirement_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Routing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Routing_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceClin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceClin", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceClin_PriceModel_ModelId",
                        column: x => x.ModelId,
                        principalTable: "PriceModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceCostLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsUnallowable = table.Column<bool>(type: "boolean", nullable: false),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    QuoteReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Destination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true, collation: "C"),
                    State = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true, collation: "C"),
                    Trips = table.Column<int>(type: "integer", nullable: false),
                    Travelers = table.Column<int>(type: "integer", nullable: false),
                    Nights = table.Column<int>(type: "integer", nullable: false),
                    AirfarePerTrip = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OtherPerTrip = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCostLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceCostLine_PriceModel_ModelId",
                        column: x => x.ModelId,
                        principalTable: "PriceModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceEscalation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceEscalation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceEscalation_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceEscalation_PriceModel_ModelId",
                        column: x => x.ModelId,
                        principalTable: "PriceModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceLaborLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    StaffName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    RateMethod = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SelectedEmployeeIds = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    ManualRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLaborLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceLaborLine_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceLaborLine_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceLaborLine_PriceModel_ModelId",
                        column: x => x.ModelId,
                        principalTable: "PriceModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PricePeriod",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false),
                    IsOption = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricePeriod", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PricePeriod_PriceModel_ModelId",
                        column: x => x.ModelId,
                        principalTable: "PriceModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceWbs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    BasisOfEstimate = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceWbs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceWbs_PriceModel_ModelId",
                        column: x => x.ModelId,
                        principalTable: "PriceModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BomComponent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    ComponentPartId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityPer = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ScrapPercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    ReferenceDesignators = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    OperationSequence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomComponent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BomComponent_BillOfMaterial_BomId",
                        column: x => x.BomId,
                        principalTable: "BillOfMaterial",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BomComponent_Part_ComponentPartId",
                        column: x => x.ComponentPartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialPurchaseLink",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequisitionLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannedOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    BinId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialPurchaseLink", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_PlannedOrder_PlannedOrderId",
                        column: x => x.PlannedOrderId,
                        principalTable: "PlannedOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_PurchaseRequisitionLine_RequisitionLin~",
                        column: x => x.RequisitionLineId,
                        principalTable: "PurchaseRequisitionLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLink_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoutingOperation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoutingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    WorkCenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SetupHours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    RunHoursPerUnit = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingOperation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoutingOperation_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingOperation_Routing_RoutingId",
                        column: x => x.RoutingId,
                        principalTable: "Routing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoutingOperation_WorkCenter_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrder",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityCompleted = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityScrapped = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    LaborChargeWbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BomId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoutingId = table.Column<Guid>(type: "uuid", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    BinId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannedOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    MaterialCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LaborCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ScrapCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CompletedCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VarianceCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrder_BillOfMaterial_BomId",
                        column: x => x.BomId,
                        principalTable: "BillOfMaterial",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_PlannedOrder_PlannedOrderId",
                        column: x => x.PlannedOrderId,
                        principalTable: "PlannedOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_Routing_RoutingId",
                        column: x => x.RoutingId,
                        principalTable: "Routing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_WbsElement_LaborChargeWbsElementId",
                        column: x => x.LaborChargeWbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrder_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceLaborHours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLaborHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceLaborHours_PriceLaborLine_LineId",
                        column: x => x.LineId,
                        principalTable: "PriceLaborLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransaction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    BinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Variance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransferGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_PurchaseReceipt_PurchaseReceiptId",
                        column: x => x.PurchaseReceiptId,
                        principalTable: "PurchaseReceipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransaction_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderComponent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityPer = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    QuantityRequired = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityIssued = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OperationSequence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderComponent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderComponent_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderComponent_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderOperation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    WorkCenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    SetupHours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    RunHoursPerUnit = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    QuantityCompleted = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    HoursReported = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderOperation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderOperation_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderOperation_WorkCenter_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderOperation_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderGenealogy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletionTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentTransactionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderGenealogy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderGenealogy_InventoryTransaction_CompletionTransacti~",
                        column: x => x.CompletionTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderGenealogy_InventoryTransaction_ComponentTransactio~",
                        column: x => x.ComponentTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderGenealogy_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderLaborReport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    QuantityCompleted = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ReportedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderLaborReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderLaborReport_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderLaborReport_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderLaborReport_WorkOrderOperation_OperationId",
                        column: x => x.OperationId,
                        principalTable: "WorkOrderOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderLaborReport_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "IX_BillOfMaterial_PartId_Revision",
                table: "BillOfMaterial",
                columns: new[] { "PartId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bin_WarehouseId_Code",
                table: "Bin",
                columns: new[] { "WarehouseId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BomComponent_BomId",
                table: "BomComponent",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_BomComponent_ComponentPartId",
                table: "BomComponent",
                column: "ComponentPartId");

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
                name: "IX_CrmAccount_CustomerId",
                table: "CrmAccount",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmAccount_Name",
                table: "CrmAccount",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CrmAccount_ParentId",
                table: "CrmAccount",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmActivity_AccountId",
                table: "CrmActivity",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmActivity_AssignedTo_IsDone",
                table: "CrmActivity",
                columns: new[] { "AssignedTo", "IsDone" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmActivity_ContactId",
                table: "CrmActivity",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmActivity_OpportunityId",
                table: "CrmActivity",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmContact_AccountId",
                table: "CrmContact",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmSettings_BpParentWbsElementId",
                table: "CrmSettings",
                column: "BpParentWbsElementId");

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
                name: "IX_GateCriterion_GateDefinitionId",
                table: "GateCriterion",
                column: "GateDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_GateDefinition_ToStage",
                table: "GateDefinition",
                column: "ToStage");

            migrationBuilder.CreateIndex(
                name: "IX_HrChangeRequest_EmployeeId_Status",
                table: "HrChangeRequest",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_BinId",
                table: "InventoryBalance",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_Lot_Serial",
                table: "InventoryBalance",
                columns: new[] { "Lot", "Serial" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_OwnerProjectId",
                table: "InventoryBalance",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_PartId_BinId",
                table: "InventoryBalance",
                columns: new[] { "PartId", "BinId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_WarehouseId",
                table: "InventoryBalance",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_BinId",
                table: "InventoryTransaction",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_JournalEntryId",
                table: "InventoryTransaction",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_Lot_Serial",
                table: "InventoryTransaction",
                columns: new[] { "Lot", "Serial" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_Number",
                table: "InventoryTransaction",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_OwnerProjectId",
                table: "InventoryTransaction",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_PartId_Date",
                table: "InventoryTransaction",
                columns: new[] { "PartId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_ProjectId_WbsElementId",
                table: "InventoryTransaction",
                columns: new[] { "ProjectId", "WbsElementId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_PurchaseOrderLineId",
                table: "InventoryTransaction",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_PurchaseReceiptId",
                table: "InventoryTransaction",
                column: "PurchaseReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_WarehouseId",
                table: "InventoryTransaction",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_WbsElementId",
                table: "InventoryTransaction",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransaction_WorkOrderId",
                table: "InventoryTransaction",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_LifeEvent_EmployeeId_Status",
                table: "LifeEvent",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_BinId",
                table: "MaterialPurchaseLink",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_OwnerProjectId",
                table: "MaterialPurchaseLink",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_PartId",
                table: "MaterialPurchaseLink",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_PlannedOrderId",
                table: "MaterialPurchaseLink",
                column: "PlannedOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_PurchaseOrderLineId",
                table: "MaterialPurchaseLink",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_RequisitionLineId",
                table: "MaterialPurchaseLink",
                column: "RequisitionLineId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLink_WarehouseId",
                table: "MaterialPurchaseLink",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_MrpRun_RunAtUtc",
                table: "MrpRun",
                column: "RunAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_AgencyAccountId",
                table: "Opportunity",
                column: "AgencyAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_BpWbsElementId",
                table: "Opportunity",
                column: "BpWbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_CaptureManagerEmail",
                table: "Opportunity",
                column: "CaptureManagerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_ContractId",
                table: "Opportunity",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_CustomerId",
                table: "Opportunity",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_IncumbentAccountId",
                table: "Opportunity",
                column: "IncumbentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_Number",
                table: "Opportunity",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_ProjectId",
                table: "Opportunity",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_ProposalManagerEmail",
                table: "Opportunity",
                column: "ProposalManagerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_SolicitationKey",
                table: "Opportunity",
                column: "SolicitationKey");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_Stage",
                table: "Opportunity",
                column: "Stage");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityCompetitor_AccountId",
                table: "OpportunityCompetitor",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityCompetitor_OpportunityId",
                table: "OpportunityCompetitor",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityGateReview_GateDefinitionId",
                table: "OpportunityGateReview",
                column: "GateDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityGateReview_OpportunityId_Status",
                table: "OpportunityGateReview",
                columns: new[] { "OpportunityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityGateScore_ReviewId",
                table: "OpportunityGateScore",
                column: "ReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityLead_OpportunityId",
                table: "OpportunityLead",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityLead_SavedSearchId",
                table: "OpportunityLead",
                column: "SavedSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityLead_SolicitationKey",
                table: "OpportunityLead",
                column: "SolicitationKey");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityLead_SourceId",
                table: "OpportunityLead",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityLead_Status",
                table: "OpportunityLead",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityStageChange_OpportunityId",
                table: "OpportunityStageChange",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityTeammate_AccountId",
                table: "OpportunityTeammate",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityTeammate_OpportunityId",
                table: "OpportunityTeammate",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Part_DefaultBinId",
                table: "Part",
                column: "DefaultBinId");

            migrationBuilder.CreateIndex(
                name: "IX_Part_DefaultWarehouseId",
                table: "Part",
                column: "DefaultWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Part_Number_Revision",
                table: "Part",
                columns: new[] { "Number", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Part_PreferredVendorId",
                table: "Part",
                column: "PreferredVendorId");

            migrationBuilder.CreateIndex(
                name: "IX_PartForecast_PartId_Date",
                table: "PartForecast",
                columns: new[] { "PartId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedOrder_PartId",
                table: "PlannedOrder",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedOrder_ProjectId",
                table: "PlannedOrder",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedOrder_RequisitionId",
                table: "PlannedOrder",
                column: "RequisitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedOrder_RunId_Status",
                table: "PlannedOrder",
                columns: new[] { "RunId", "Status" });

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
                name: "IX_PriceClin_ModelId",
                table: "PriceClin",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCostLine_ModelId",
                table: "PriceCostLine",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceEscalation_LaborCategoryId",
                table: "PriceEscalation",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceEscalation_ModelId",
                table: "PriceEscalation",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLaborHours_LineId_PeriodId",
                table: "PriceLaborHours",
                columns: new[] { "LineId", "PeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLaborLine_EmployeeId",
                table: "PriceLaborLine",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLaborLine_LaborCategoryId",
                table: "PriceLaborLine",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLaborLine_ModelId",
                table: "PriceLaborLine",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceModel_ProposalId",
                table: "PriceModel",
                column: "ProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_PricePeriod_ModelId",
                table: "PricePeriod",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceWbs_ModelId",
                table: "PriceWbs",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMaterialRequirement_PartId_IsClosed",
                table: "ProjectMaterialRequirement",
                columns: new[] { "PartId", "IsClosed" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMaterialRequirement_ProjectId",
                table: "ProjectMaterialRequirement",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMaterialRequirement_WbsElementId",
                table: "ProjectMaterialRequirement",
                column: "WbsElementId");

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
                name: "IX_Proposal_Number",
                table: "Proposal",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Proposal_OpportunityId",
                table: "Proposal",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Routing_PartId_Revision",
                table: "Routing",
                columns: new[] { "PartId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingOperation_LaborCategoryId",
                table: "RoutingOperation",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingOperation_RoutingId",
                table: "RoutingOperation",
                column: "RoutingId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutingOperation_WorkCenterId",
                table: "RoutingOperation",
                column: "WorkCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetFlagExplanation_TimesheetId_RuleKey_FactsHash",
                table: "TimesheetFlagExplanation",
                columns: new[] { "TimesheetId", "RuleKey", "FactsHash" });

            migrationBuilder.CreateIndex(
                name: "IX_Warehouse_Code",
                table: "Warehouse",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCenter_Code",
                table: "WorkCenter",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCenter_LaborCategoryId",
                table: "WorkCenter",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_BinId",
                table: "WorkOrder",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_BomId",
                table: "WorkOrder",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_LaborChargeWbsElementId",
                table: "WorkOrder",
                column: "LaborChargeWbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_Number",
                table: "WorkOrder",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_PartId",
                table: "WorkOrder",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_PlannedOrderId",
                table: "WorkOrder",
                column: "PlannedOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_ProjectId",
                table: "WorkOrder",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_RoutingId",
                table: "WorkOrder",
                column: "RoutingId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_Status_PartId",
                table: "WorkOrder",
                columns: new[] { "Status", "PartId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_WarehouseId",
                table: "WorkOrder",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrder_WbsElementId",
                table: "WorkOrder",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderComponent_PartId",
                table: "WorkOrderComponent",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderComponent_WorkOrderId",
                table: "WorkOrderComponent",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderGenealogy_CompletionTransactionId",
                table: "WorkOrderGenealogy",
                column: "CompletionTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderGenealogy_ComponentTransactionId",
                table: "WorkOrderGenealogy",
                column: "ComponentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderGenealogy_WorkOrderId",
                table: "WorkOrderGenealogy",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderLaborReport_EmployeeId_WorkDate",
                table: "WorkOrderLaborReport",
                columns: new[] { "EmployeeId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderLaborReport_JournalEntryId",
                table: "WorkOrderLaborReport",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderLaborReport_OperationId",
                table: "WorkOrderLaborReport",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderLaborReport_WorkOrderId",
                table: "WorkOrderLaborReport",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderOperation_LaborCategoryId",
                table: "WorkOrderOperation",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderOperation_WorkCenterId",
                table: "WorkOrderOperation",
                column: "WorkCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderOperation_WorkOrderId",
                table: "WorkOrderOperation",
                column: "WorkOrderId");
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
                name: "BomComponent");

            migrationBuilder.DropTable(
                name: "ChecklistTask");

            migrationBuilder.DropTable(
                name: "ChecklistTemplateTask");

            migrationBuilder.DropTable(
                name: "CompensationChange");

            migrationBuilder.DropTable(
                name: "CrmActivity");

            migrationBuilder.DropTable(
                name: "CrmSettings");

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
                name: "GateCriterion");

            migrationBuilder.DropTable(
                name: "HrChangeRequest");

            migrationBuilder.DropTable(
                name: "InventoryBalance");

            migrationBuilder.DropTable(
                name: "MaterialPurchaseLink");

            migrationBuilder.DropTable(
                name: "OpportunityCompetitor");

            migrationBuilder.DropTable(
                name: "OpportunityGateScore");

            migrationBuilder.DropTable(
                name: "OpportunityLead");

            migrationBuilder.DropTable(
                name: "OpportunityStageChange");

            migrationBuilder.DropTable(
                name: "OpportunityTeammate");

            migrationBuilder.DropTable(
                name: "PartForecast");

            migrationBuilder.DropTable(
                name: "PriceClin");

            migrationBuilder.DropTable(
                name: "PriceCostLine");

            migrationBuilder.DropTable(
                name: "PriceEscalation");

            migrationBuilder.DropTable(
                name: "PriceLaborHours");

            migrationBuilder.DropTable(
                name: "PricePeriod");

            migrationBuilder.DropTable(
                name: "PriceWbs");

            migrationBuilder.DropTable(
                name: "ProjectMaterialRequirement");

            migrationBuilder.DropTable(
                name: "ProjectNarrative");

            migrationBuilder.DropTable(
                name: "RoutingOperation");

            migrationBuilder.DropTable(
                name: "TimesheetFlagExplanation");

            migrationBuilder.DropTable(
                name: "WorkOrderComponent");

            migrationBuilder.DropTable(
                name: "WorkOrderGenealogy");

            migrationBuilder.DropTable(
                name: "WorkOrderLaborReport");

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
                name: "CrmContact");

            migrationBuilder.DropTable(
                name: "Position");

            migrationBuilder.DropTable(
                name: "OpportunityGateReview");

            migrationBuilder.DropTable(
                name: "SavedLeadSearch");

            migrationBuilder.DropTable(
                name: "PriceLaborLine");

            migrationBuilder.DropTable(
                name: "InventoryTransaction");

            migrationBuilder.DropTable(
                name: "WorkOrderOperation");

            migrationBuilder.DropTable(
                name: "BenefitTier");

            migrationBuilder.DropTable(
                name: "LifeEvent");

            migrationBuilder.DropTable(
                name: "OpenEnrollmentWindow");

            migrationBuilder.DropTable(
                name: "ChecklistTemplate");

            migrationBuilder.DropTable(
                name: "GateDefinition");

            migrationBuilder.DropTable(
                name: "PriceModel");

            migrationBuilder.DropTable(
                name: "WorkCenter");

            migrationBuilder.DropTable(
                name: "WorkOrder");

            migrationBuilder.DropTable(
                name: "BenefitPlan");

            migrationBuilder.DropTable(
                name: "Proposal");

            migrationBuilder.DropTable(
                name: "BillOfMaterial");

            migrationBuilder.DropTable(
                name: "PlannedOrder");

            migrationBuilder.DropTable(
                name: "Routing");

            migrationBuilder.DropTable(
                name: "Opportunity");

            migrationBuilder.DropTable(
                name: "MrpRun");

            migrationBuilder.DropTable(
                name: "Part");

            migrationBuilder.DropTable(
                name: "CrmAccount");

            migrationBuilder.DropTable(
                name: "Bin");

            migrationBuilder.DropTable(
                name: "Warehouse");

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
