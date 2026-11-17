using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    FormatId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, collation: "C"),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    ControlTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    WarningsJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    UnmappedAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    UnmappedCount = table.Column<int>(type: "integer", nullable: false),
                    IncludedSubledgerJournals = table.Column<bool>(type: "boolean", nullable: false),
                    IsRepeat = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    BlobKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingExportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    MapKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    SourceKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    SourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    DestinationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    DestinationCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingExportedRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false, collation: "C"),
                    FirstRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExportCount = table.Column<int>(type: "integer", nullable: false),
                    FirstExportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastExportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
