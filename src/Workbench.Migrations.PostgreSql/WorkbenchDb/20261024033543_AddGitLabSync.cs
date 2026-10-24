using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    TokenProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    TokenHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    CaCertificatePem = table.Column<string>(type: "character varying(64000)", maxLength: 64000, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectedAsUsername = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    ConnectedAsName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    ServerVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    LastTestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTestOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastTestMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitLabProjectLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitLabProjectId = table.Column<long>(type: "bigint", nullable: false),
                    GitLabProjectPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    GitLabProjectUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    GitLabGroupId = table.Column<long>(type: "bigint", nullable: true),
                    GitLabGroupPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    PhaseMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PhaseLevels = table.Column<int>(type: "integer", nullable: false),
                    MultipleAssignees = table.Column<bool>(type: "boolean", nullable: false),
                    BlockingLinks = table.Column<bool>(type: "boolean", nullable: false),
                    WebhookSecretProtected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    HookId = table.Column<long>(type: "bigint", nullable: true),
                    GroupHookId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    LastErrorHint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ReconcileCursorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastReconcileAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    GitLabUserId = table.Column<long>(type: "bigint", nullable: false),
                    GitLabUsername = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    GitLabName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    MatchedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabUserMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitLabItemLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GitLabId = table.Column<long>(type: "bigint", nullable: false),
                    Iid = table.Column<long>(type: "bigint", nullable: false),
                    WebUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    BaseTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    BaseDescription = table.Column<string>(type: "character varying(64000)", maxLength: 64000, nullable: true, collation: "C"),
                    BaseAssignees = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    BaseStart = table.Column<DateOnly>(type: "date", nullable: true),
                    BaseDue = table.Column<DateOnly>(type: "date", nullable: true),
                    BaseState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    BaseWbsCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    BaseEstimateHours = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    BaseParentId = table.Column<long>(type: "bigint", nullable: true),
                    BaseBlocks = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    BasePercent = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    PercentBeforeClose = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    GitLabUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkbenchModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsRemoved = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<long>(type: "bigint", nullable: false),
                    Iid = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    WebUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    PhaseItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Author = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Iid = table.Column<long>(type: "bigint", nullable: true),
                    ObjectKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    WorkbenchValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    GitLabValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Outcome = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    Hint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    EventKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
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
