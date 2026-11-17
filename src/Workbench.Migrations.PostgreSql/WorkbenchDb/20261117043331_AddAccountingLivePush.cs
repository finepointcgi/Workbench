using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddAccountingLivePush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountingConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    IsSandbox = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, collation: "C"),
                    CompanyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    CompanyName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    AccessTokenProtected = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true, collation: "C"),
                    RefreshTokenProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    AccessExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RefreshExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChoicesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    LastRefreshedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true, collation: "C"),
                    LastSyncMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ConnectedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ConnectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TokenVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingOAuthStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingOAuthStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingProviderLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    ProviderNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingProviderLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPushRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, collation: "C"),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, collation: "C"),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    NotSent = table.Column<int>(type: "integer", nullable: false),
                    ContactsCreated = table.Column<int>(type: "integer", nullable: false),
                    RetryOfRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreateMissingContacts = table.Column<bool>(type: "boolean", nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    StartedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPushRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPushItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    ProviderNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPushItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingPushItems_AccountingPushRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AccountingPushRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_Target",
                table: "AccountingConnections",
                column: "Target",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOAuthStates_StateHash",
                table: "AccountingOAuthStates",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingProviderLinks_Target_Kind_RecordId",
                table: "AccountingProviderLinks",
                columns: new[] { "Target", "Kind", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPushItems_RunId_Status",
                table: "AccountingPushItems",
                columns: new[] { "RunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingPushRuns_Target_StartedAtUtc",
                table: "AccountingPushRuns",
                columns: new[] { "Target", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountingConnections");

            migrationBuilder.DropTable(
                name: "AccountingOAuthStates");

            migrationBuilder.DropTable(
                name: "AccountingProviderLinks");

            migrationBuilder.DropTable(
                name: "AccountingPushItems");

            migrationBuilder.DropTable(
                name: "AccountingPushRuns");
        }
    }
}
