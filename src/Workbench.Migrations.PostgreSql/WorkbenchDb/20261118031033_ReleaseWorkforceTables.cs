using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <summary>
    /// The <c>workforce</c> license module owns the tables of HR (personnel records, positions, compensation, benefits, onboarding checklists) and payroll (pay rate history, payroll runs and registers, provider settings, the Paylocity exchange and payroll file layouts). This migration only moves their <b>EF ownership</b> out of core's
    /// model snapshot: it changes no table and drops no data. Databases made before it keep the tables with their rows; the module's own
    /// migration creates them only where they do not exist (<c>__EFMigrationsHistory_workforce</c>). Core's earlier migrations no longer
    /// create these tables either, so a new install without the module has none. See docs/operations/module-extraction-workforce.md.
    /// </summary>
    public partial class ReleaseWorkforceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: ownership of these tables moved to the workforce module; no table is touched.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: nothing was dropped, so there is nothing to put back.
        }
    }
}
