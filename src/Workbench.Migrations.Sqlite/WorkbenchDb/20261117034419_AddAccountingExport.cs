using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddAccountingExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountingExportRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FormatId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ControlTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Checksum = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: true),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnmappedAllowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnmappedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludedSubledgerJournals = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRepeat = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    BlobKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingExportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MapKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DestinationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DestinationCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingExportedRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordNumber = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    FirstRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExportCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstExportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastExportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingExportedRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingExportedRecords_AccountingExportRuns_FirstRunId",
                        column: x => x.FirstRunId,
                        principalTable: "AccountingExportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountingExportedRecords_AccountingExportRuns_LastRunId",
                        column: x => x.LastRunId,
                        principalTable: "AccountingExportRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingExportedRecords_FirstRunId",
                table: "AccountingExportedRecords",
                column: "FirstRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingExportedRecords_LastRunId",
                table: "AccountingExportedRecords",
                column: "LastRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingExportedRecords_Target_Kind_RecordId",
                table: "AccountingExportedRecords",
                columns: new[] { "Target", "Kind", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingExportRuns_Target_Kind_CreatedAtUtc",
                table: "AccountingExportRuns",
                columns: new[] { "Target", "Kind", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingMappings_Target_MapKind_SourceKey",
                table: "AccountingMappings",
                columns: new[] { "Target", "MapKind", "SourceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingExportedRecords");

            migrationBuilder.DropTable(
                name: "AccountingMappings");

            migrationBuilder.DropTable(
                name: "AccountingExportRuns");
        }
    }
}
