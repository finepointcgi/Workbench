using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <summary>
    /// The <c>govcon</c> license module owns the tables of government billing: contract financing settings, SF 1443 progress payment requests, performance-based payment events and PIEE submissions. This migration only moves their <b>EF ownership</b> out of core's
    /// model snapshot: it changes no table and drops no data. Databases made before it keep the tables with their rows; the module's own
    /// migration creates them only where they do not exist (<c>__EFMigrationsHistory_govcon</c>). Core's earlier migrations no longer
    /// create these tables either, so a new install without the module has none. See docs/operations/module-extraction-billing.md.
    /// </summary>
    public partial class ReleaseGovernmentBillingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: ownership of these tables moved to the govcon module; no table is touched.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: nothing was dropped, so there is nothing to put back.
        }
    }
}
