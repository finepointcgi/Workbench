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

            migrationBuilder.CreateIndex(
                name: "IX_LaborCostAdjustment_EmployeeId",
                table: "LaborCostAdjustment",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborCostAdjustment_TimesheetId_TimesheetRevision",
                table: "LaborCostAdjustment",
                columns: new[] { "TimesheetId", "TimesheetRevision" });

            // The data step that used to be here (a pay rate history row for each profile saved before the history existed, and the matching cost rate) moved with
            // the pay rate history to the workforce module: EmployeePayRate is no longer created by core. PayRates.BackfillAsync in the module does the same.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LaborCostAdjustment");

        }
    }
}
