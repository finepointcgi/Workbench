using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class GoLiveReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EligibleEmploymentTypes",
                table: "LeavePolicy",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<bool>(
                name: "EligibleExempt",
                table: "LeavePolicy",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EligibleOrgUnitIds",
                table: "LeavePolicy",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "EligiblePayGroupIds",
                table: "LeavePolicy",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<decimal>(
                name: "CostBasis",
                table: "InvoiceLine",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FiscalYear",
                table: "InvoiceLine",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Convention",
                table: "FixedAsset",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "OpeningAccumulatedDepreciation",
                table: "FixedAsset",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "OpeningBalanceDate",
                table: "FixedAsset",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmploymentType",
                table: "EmployeePayProfile",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "FullTime");

            migrationBuilder.AddColumn<bool>(
                name: "CheckAtRequisition",
                table: "Ceiling",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "CapitalizationThreshold",
                table: "AssetCategory",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Convention",
                table: "AssetCategory",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ComplianceChecklistEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChecklistKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ItemKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    ReviewedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceChecklistEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmployeePayRate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PayType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AnnualSalary = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    HourlyRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeePayRate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeePayRate_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MigrationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CutoverDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ParallelPeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationSettings_FiscalPeriod_ParallelPeriodId",
                        column: x => x.ParallelPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MigrationStepRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationStepRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollFileLayout",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    HoursLayoutJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true, collation: "C"),
                    RegisterLayoutJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollFileLayout", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Report = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    MappingJson = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: false, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationMapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Report = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: false),
                    MappingId = table.Column<Guid>(type: "uuid", nullable: true),
                    MappingName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    MappingJson = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: false, collation: "C"),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    FileContent = table.Column<byte[]>(type: "bytea", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyCount = table.Column<int>(type: "integer", nullable: false),
                    MatchedCount = table.Column<int>(type: "integer", nullable: false),
                    DifferentCount = table.Column<int>(type: "integer", nullable: false),
                    OnlyCostpointCount = table.Column<int>(type: "integer", nullable: false),
                    OnlyWorkbenchCount = table.Column<int>(type: "integer", nullable: false),
                    ProblemCount = table.Column<int>(type: "integer", nullable: false),
                    PrimaryField = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    CostpointTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    WorkbenchTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ResultJson = table.Column<string>(type: "character varying(10000000)", maxLength: 10000000, nullable: false, collation: "C"),
                    RunBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    RunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SignedOffBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SignedOffAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignOffNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationRun_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignInSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SsoEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ProviderKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Authority = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C"),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ProtectedClientSecret = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    ClientSecretSetAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    RequireOrganizationIdp = table.Column<bool>(type: "boolean", nullable: false),
                    MfaRequirement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AcceptedAmrValues = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    AcceptedAcrValues = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    RequestedAcrValues = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    JitMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AllowedEmailDomains = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    JitDefaultRoles = table.Column<int>(type: "integer", nullable: false),
                    GroupsClaim = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    SyncRolesFromGroups = table.Column<bool>(type: "boolean", nullable: false),
                    ReauthenticationMinutes = table.Column<int>(type: "integer", nullable: false),
                    SessionMaxHours = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SsoGroupMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    Roles = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsoGroupMapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    PagePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    FromError = table.Column<bool>(type: "boolean", nullable: false),
                    ReporterEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ReporterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ScreenshotKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ScreenshotFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    ScreenshotContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    ScreenshotBytes = table.Column<long>(type: "bigint", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    HandledBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserHelpState",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CompletedTours = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    WhatsNewSeen = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserHelpState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntry_WorkDate",
                table: "TimeEntry",
                column: "WorkDate");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceChecklistEntry_ChecklistKey_ItemKey_ReviewedAtUtc",
                table: "ComplianceChecklistEntry",
                columns: new[] { "ChecklistKey", "ItemKey", "ReviewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeePayRate_EmployeeId_EffectiveFrom",
                table: "EmployeePayRate",
                columns: new[] { "EmployeeId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationSettings_ParallelPeriodId",
                table: "MigrationSettings",
                column: "ParallelPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStepRecord_StepKey",
                table: "MigrationStepRecord",
                column: "StepKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollFileLayout_ProviderKey",
                table: "PayrollFileLayout",
                column: "ProviderKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationMapping_Report_Name",
                table: "ReconciliationMapping",
                columns: new[] { "Report", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRun_FiscalPeriodId_Report_RunAtUtc",
                table: "ReconciliationRun",
                columns: new[] { "FiscalPeriodId", "Report", "RunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SsoGroupMapping_GroupValue",
                table: "SsoGroupMapping",
                column: "GroupValue",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportRequest_Number",
                table: "SupportRequest",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportRequest_ReporterEmail",
                table: "SupportRequest",
                column: "ReporterEmail");

            migrationBuilder.CreateIndex(
                name: "IX_SupportRequest_Status",
                table: "SupportRequest",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserHelpState_UserEmail",
                table: "UserHelpState",
                column: "UserEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComplianceChecklistEntry");

            migrationBuilder.DropTable(
                name: "EmployeePayRate");

            migrationBuilder.DropTable(
                name: "MigrationSettings");

            migrationBuilder.DropTable(
                name: "MigrationStepRecord");

            migrationBuilder.DropTable(
                name: "PayrollFileLayout");

            migrationBuilder.DropTable(
                name: "ReconciliationMapping");

            migrationBuilder.DropTable(
                name: "ReconciliationRun");

            migrationBuilder.DropTable(
                name: "SignInSettings");

            migrationBuilder.DropTable(
                name: "SsoGroupMapping");

            migrationBuilder.DropTable(
                name: "SupportRequest");

            migrationBuilder.DropTable(
                name: "UserHelpState");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntry_WorkDate",
                table: "TimeEntry");

            migrationBuilder.DropColumn(
                name: "EligibleEmploymentTypes",
                table: "LeavePolicy");

            migrationBuilder.DropColumn(
                name: "EligibleExempt",
                table: "LeavePolicy");

            migrationBuilder.DropColumn(
                name: "EligibleOrgUnitIds",
                table: "LeavePolicy");

            migrationBuilder.DropColumn(
                name: "EligiblePayGroupIds",
                table: "LeavePolicy");

            migrationBuilder.DropColumn(
                name: "CostBasis",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "FiscalYear",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "Convention",
                table: "FixedAsset");

            migrationBuilder.DropColumn(
                name: "OpeningAccumulatedDepreciation",
                table: "FixedAsset");

            migrationBuilder.DropColumn(
                name: "OpeningBalanceDate",
                table: "FixedAsset");

            migrationBuilder.DropColumn(
                name: "EmploymentType",
                table: "EmployeePayProfile");

            migrationBuilder.DropColumn(
                name: "CheckAtRequisition",
                table: "Ceiling");

            migrationBuilder.DropColumn(
                name: "CapitalizationThreshold",
                table: "AssetCategory");

            migrationBuilder.DropColumn(
                name: "Convention",
                table: "AssetCategory");
        }
    }
}
