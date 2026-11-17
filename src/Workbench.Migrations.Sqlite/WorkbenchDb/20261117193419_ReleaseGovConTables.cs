using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <summary>
    /// The government contracting module (<c>govcon</c>) owns the tables of CPSR procurement files, CAS disclosure statements, CMMC
    /// assessments and incurred cost submissions. This migration only moves their <b>EF ownership</b> out of core's model snapshot:
    /// it changes no table and drops no data. Databases made before it keep the tables with their rows; the module's own first
    /// migration creates them only where they do not exist (<c>__EFMigrationsHistory_govcon</c>). Core's earlier migrations no longer
    /// create these tables either, so a new install without the module has none. See docs/operations/module-extraction-govcon.md.
    /// </summary>
    public partial class ReleaseGovConTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: ownership of the government contracting tables moved to the module; no table is touched.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: nothing was dropped, so there is nothing to put back.
        }
    }
}
