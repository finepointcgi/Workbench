using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EligibleExempt",
                table: "LeavePolicy",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EligibleOrgUnitIds",
                table: "LeavePolicy",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EligiblePayGroupIds",
                table: "LeavePolicy",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CostBasis",
                table: "InvoiceLine",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FiscalYear",
                table: "InvoiceLine",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Convention",
                table: "FixedAsset",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "OpeningAccumulatedDepreciation",
                table: "FixedAsset",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "OpeningBalanceDate",
                table: "FixedAsset",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmploymentType",
                table: "EmployeePayProfile",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "FullTime");

            migrationBuilder.AddColumn<bool>(
                name: "CheckAtRequisition",
                table: "Ceiling",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "CapitalizationThreshold",
                table: "AssetCategory",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Convention",
                table: "AssetCategory",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ComplianceChecklistEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChecklistKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceChecklistEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmployeePayRate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PayType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AnnualSalary = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    HourlyRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CutoverDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ParallelPeriodId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StepKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationStepRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollFileLayout",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    HoursLayoutJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    RegisterLayoutJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollFileLayout", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Report = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MappingJson = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationMapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Report = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FromDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    MappingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MappingName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MappingJson = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileContent = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    KeyCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DifferentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlyCostpointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlyWorkbenchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProblemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PrimaryField = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CostpointTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    WorkbenchTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", maxLength: 10000000, nullable: false),
                    RunBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    RunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SignedOffBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    SignedOffAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SignOffNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SsoEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProviderKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Authority = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ClientSecretSetAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RequireOrganizationIdp = table.Column<bool>(type: "INTEGER", nullable: false),
                    MfaRequirement = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AcceptedAmrValues = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AcceptedAcrValues = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RequestedAcrValues = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    JitMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AllowedEmailDomains = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    JitDefaultRoles = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupsClaim = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SyncRolesFromGroups = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReauthenticationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionMaxHours = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SsoGroupMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupValue = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Roles = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsoGroupMapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PagePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FromError = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReporterEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ReporterName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ScreenshotKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ScreenshotFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ScreenshotContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ScreenshotBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    HandledBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserHelpState",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CompletedTours = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    WhatsNewSeen = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
