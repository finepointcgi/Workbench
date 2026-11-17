using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsSandbox = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CompanyId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AccessExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RefreshExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChoicesJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastRefreshedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    LastSyncMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ConnectedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TokenVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingOAuthStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingOAuthStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingProviderLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingProviderLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPushRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    NotSent = table.Column<int>(type: "INTEGER", nullable: false),
                    ContactsCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryOfRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreateMissingContacts = table.Column<bool>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    StartedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPushRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountingPushItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordNumber = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProviderNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
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
