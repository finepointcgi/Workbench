using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class PayRateHistoryLaborAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LaborCostAdjustment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimesheetRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RateEffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    WorkPeriods = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Redirected = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborCostAdjustment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LaborCostAdjustment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborCostAdjustment_Timesheet_TimesheetId",
                        column: x => x.TimesheetId,
                        principalTable: "Timesheet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollProviderConnection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ProtectedCredentials = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ConnectedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastCheckMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollProviderConnection", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LaborCostAdjustment_EmployeeId",
                table: "LaborCostAdjustment",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborCostAdjustment_TimesheetId_TimesheetRevision",
                table: "LaborCostAdjustment",
                columns: new[] { "TimesheetId", "TimesheetRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollProviderConnection_ProviderKey",
                table: "PayrollProviderConnection",
                column: "ProviderKey",
                unique: true);

            // Data: profiles saved before pay rate history get one history row (the rate on file) from the hire date, or the
            // first day with time when that's earlier; employees with a pay rate but no labor cost rate get the matching
            // cost rate (hourly pay, or salary / 2,080) from the same date. Idempotent (PayRates.BackfillAsync does the same).
            migrationBuilder.Sql("""
                INSERT INTO "EmployeePayRate" ("Id", "EmployeeId", "EffectiveFrom", "PayType", "AnnualSalary", "HourlyRate", "Reason", "CreatedBy", "CreatedAtUtc")
                SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-'
                           || substr('89AB', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
                       p."EmployeeId",
                       CASE WHEN ft."First" IS NOT NULL AND ft."First" < e."HireDate" THEN ft."First" ELSE e."HireDate" END,
                       p."PayType", p."AnnualSalary", p."HourlyRate", 'Rate on file before pay rate history', COALESCE(p."UpdatedBy", 'system'), p."UpdatedAtUtc"
                FROM "EmployeePayProfile" p
                JOIN "Employee" e ON e."Id" = p."EmployeeId"
                LEFT JOIN (SELECT t."EmployeeId", MIN(x."WorkDate") AS "First"
                           FROM "TimeEntry" x JOIN "Timesheet" t ON t."Id" = x."TimesheetId"
                           WHERE CAST(x."Hours" AS REAL) <> 0
                           GROUP BY t."EmployeeId") ft ON ft."EmployeeId" = p."EmployeeId"
                WHERE (p."AnnualSalary" IS NOT NULL OR p."HourlyRate" IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM "EmployeePayRate" r WHERE r."EmployeeId" = p."EmployeeId");
                """);
            migrationBuilder.Sql("""
                INSERT INTO "EmployeeCostRate" ("Id", "EmployeeId", "EffectiveFrom", "HourlyCost")
                SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-'
                           || substr('89AB', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
                       r."EmployeeId", r."EffectiveFrom",
                       CAST(CASE WHEN r."PayType" = 'Salaried' THEN round(CAST(r."AnnualSalary" AS REAL) / 2080.0, 4) ELSE CAST(r."HourlyRate" AS REAL) END AS TEXT)
                FROM "EmployeePayRate" r
                WHERE r."EffectiveFrom" = (SELECT MIN(r2."EffectiveFrom") FROM "EmployeePayRate" r2 WHERE r2."EmployeeId" = r."EmployeeId")
                  AND r."Id" = (SELECT r3."Id" FROM "EmployeePayRate" r3 WHERE r3."EmployeeId" = r."EmployeeId" AND r3."EffectiveFrom" = r."EffectiveFrom" ORDER BY r3."CreatedAtUtc" DESC LIMIT 1)
                  AND ((r."PayType" = 'Salaried' AND r."AnnualSalary" IS NOT NULL) OR (r."PayType" = 'Hourly' AND r."HourlyRate" IS NOT NULL))
                  AND NOT EXISTS (SELECT 1 FROM "EmployeeCostRate" c WHERE c."EmployeeId" = r."EmployeeId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LaborCostAdjustment");

            migrationBuilder.DropTable(
                name: "PayrollProviderConnection");
        }
    }
}
