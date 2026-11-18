using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <summary>
    /// The <c>accounting</c> license module owns the tables of cash and bank reconciliation, bank files, fixed assets and tax books, corporate budgets, planning cycles and recurring journals, and sales and use tax. This migration only moves their <b>EF ownership</b> out of core's
    /// model snapshot: it changes no table and drops no data. Databases made before it keep the tables with their rows; the module's own
    /// migration creates them only where they do not exist (<c>__EFMigrationsHistory_accounting</c>). Core's earlier migrations no longer
    /// create these tables either, so a new install without the module has none. See docs/operations/module-extraction-accounting.md.
    /// </summary>
    public partial class ReleaseAccountingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: ownership of these tables moved to the accounting module; no table is touched.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: nothing was dropped, so there is nothing to put back.
        }
    }
}
