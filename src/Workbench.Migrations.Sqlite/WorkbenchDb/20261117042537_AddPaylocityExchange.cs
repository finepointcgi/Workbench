using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddPaylocityExchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaylocityCostCenterMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaylocityCostCenterMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaylocityCostCenterMapping_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaylocityRosterSetting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnedFields = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaylocityRosterSetting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollExportRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    LineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EmployeeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalHours = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: false),
                    FlaggedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsReExport = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ExportedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ExportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollExportRecord", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollExportRecord_PayrollRun_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaylocityCostCenterMapping_OrgUnitId",
                table: "PaylocityCostCenterMapping",
                column: "OrgUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollExportRecord_PayGroupId_PayPeriodId",
                table: "PayrollExportRecord",
                columns: new[] { "PayGroupId", "PayPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollExportRecord_RunId_Sequence",
                table: "PayrollExportRecord",
                columns: new[] { "RunId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaylocityCostCenterMapping");

            migrationBuilder.DropTable(
                name: "PaylocityRosterSetting");

            migrationBuilder.DropTable(
                name: "PayrollExportRecord");
        }
    }
}
