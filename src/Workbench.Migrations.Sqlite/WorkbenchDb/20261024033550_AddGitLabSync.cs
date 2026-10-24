using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddGitLabSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitLabConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TokenProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    TokenHint = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CaCertificatePem = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConnectedAsUsername = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ConnectedAsName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ServerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastTestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTestOk = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitLabProjectLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GitLabProjectId = table.Column<long>(type: "INTEGER", nullable: false),
                    GitLabProjectPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    GitLabProjectUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    GitLabGroupId = table.Column<long>(type: "INTEGER", nullable: true),
                    GitLabGroupPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PhaseMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PhaseLevels = table.Column<int>(type: "INTEGER", nullable: false),
                    MultipleAssignees = table.Column<bool>(type: "INTEGER", nullable: false),
                    BlockingLinks = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebhookSecretProtected = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    HookId = table.Column<long>(type: "INTEGER", nullable: true),
                    GroupHookId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastErrorHint = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReconcileCursorUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReconcileAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabProjectLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitLabProjectLinks_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitLabUserMaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    GitLabUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    GitLabUsername = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    GitLabName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    MatchedBy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabUserMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitLabItemLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    GitLabId = table.Column<long>(type: "INTEGER", nullable: false),
                    Iid = table.Column<long>(type: "INTEGER", nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    BaseTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BaseDescription = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: true),
                    BaseAssignees = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    BaseStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    BaseDue = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    BaseState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    BaseWbsCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BaseEstimateHours = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: true),
                    BaseParentId = table.Column<long>(type: "INTEGER", nullable: true),
                    BaseBlocks = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    BasePercent = table.Column<decimal>(type: "TEXT", precision: 5, scale: 1, nullable: true),
                    PercentBeforeClose = table.Column<decimal>(type: "TEXT", precision: 5, scale: 1, nullable: true),
                    GitLabUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WorkbenchModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabItemLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitLabItemLinks_GitLabProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "GitLabProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitLabPendingIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueId = table.Column<long>(type: "INTEGER", nullable: false),
                    Iid = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PhaseItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabPendingIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitLabPendingIssues_GitLabProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "GitLabProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitLabSyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Iid = table.Column<long>(type: "INTEGER", nullable: true),
                    ObjectKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WorkbenchValue = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    GitLabValue = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Hint = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabSyncConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitLabSyncConflicts_GitLabProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "GitLabProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitLabWebhookReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabWebhookReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitLabWebhookReceipts_GitLabProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "GitLabProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitLabItemLinks_ItemId",
                table: "GitLabItemLinks",
                column: "ItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabItemLinks_LinkId_Kind_GitLabId",
                table: "GitLabItemLinks",
                columns: new[] { "LinkId", "Kind", "GitLabId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabPendingIssues_LinkId_IssueId",
                table: "GitLabPendingIssues",
                columns: new[] { "LinkId", "IssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabProjectLinks_ProjectId",
                table: "GitLabProjectLinks",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabProjectLinks_SheetId",
                table: "GitLabProjectLinks",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_GitLabSyncConflicts_ItemId_Field_Status",
                table: "GitLabSyncConflicts",
                columns: new[] { "ItemId", "Field", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GitLabSyncConflicts_LinkId_Status_CreatedAtUtc",
                table: "GitLabSyncConflicts",
                columns: new[] { "LinkId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GitLabUserMaps_Email",
                table: "GitLabUserMaps",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabUserMaps_GitLabUserId",
                table: "GitLabUserMaps",
                column: "GitLabUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabWebhookReceipts_LinkId_DedupKey",
                table: "GitLabWebhookReceipts",
                columns: new[] { "LinkId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitLabWebhookReceipts_ReceivedAtUtc",
                table: "GitLabWebhookReceipts",
                column: "ReceivedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitLabConnections");

            migrationBuilder.DropTable(
                name: "GitLabItemLinks");

            migrationBuilder.DropTable(
                name: "GitLabPendingIssues");

            migrationBuilder.DropTable(
                name: "GitLabSyncConflicts");

            migrationBuilder.DropTable(
                name: "GitLabUserMaps");

            migrationBuilder.DropTable(
                name: "GitLabWebhookReceipts");

            migrationBuilder.DropTable(
                name: "GitLabProjectLinks");
        }
    }
}
