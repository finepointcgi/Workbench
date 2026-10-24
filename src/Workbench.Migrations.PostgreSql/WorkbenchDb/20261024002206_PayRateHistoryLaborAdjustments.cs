using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimesheetRevision = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RateEffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WorkPeriods = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Redirected = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    ProtectedCredentials = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ConnectedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ConnectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastCheckMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
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
                SELECT gen_random_uuid(), p."EmployeeId", LEAST(e."HireDate", COALESCE(ft."First", e."HireDate")),
                       p."PayType", p."AnnualSalary", p."HourlyRate", 'Rate on file before pay rate history', COALESCE(p."UpdatedBy", 'system'), p."UpdatedAtUtc"
                FROM "EmployeePayProfile" p
                JOIN "Employee" e ON e."Id" = p."EmployeeId"
                LEFT JOIN (SELECT t."EmployeeId", MIN(x."WorkDate") AS "First"
                           FROM "TimeEntry" x JOIN "Timesheet" t ON t."Id" = x."TimesheetId"
                           WHERE x."Hours" <> 0
                           GROUP BY t."EmployeeId") ft ON ft."EmployeeId" = p."EmployeeId"
                WHERE (p."AnnualSalary" IS NOT NULL OR p."HourlyRate" IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM "EmployeePayRate" r WHERE r."EmployeeId" = p."EmployeeId");
                """);
            migrationBuilder.Sql("""
                INSERT INTO "EmployeeCostRate" ("Id", "EmployeeId", "EffectiveFrom", "HourlyCost")
                SELECT DISTINCT ON (r."EmployeeId") gen_random_uuid(), r."EmployeeId", r."EffectiveFrom",
                       CASE WHEN r."PayType" = 'Salaried' THEN round(r."AnnualSalary" / 2080.0, 4) ELSE r."HourlyRate" END
                FROM "EmployeePayRate" r
                WHERE ((r."PayType" = 'Salaried' AND r."AnnualSalary" IS NOT NULL) OR (r."PayType" = 'Hourly' AND r."HourlyRate" IS NOT NULL))
                  AND NOT EXISTS (SELECT 1 FROM "EmployeeCostRate" c WHERE c."EmployeeId" = r."EmployeeId")
                ORDER BY r."EmployeeId", r."EffectiveFrom", r."CreatedAtUtc" DESC;
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
