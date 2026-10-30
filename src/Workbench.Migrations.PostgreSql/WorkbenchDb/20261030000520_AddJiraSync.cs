using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddJiraSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JiraConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Flavor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SiteUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    TokenProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    TokenHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectedAsName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    ConnectedAsAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
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
                    table.PrimaryKey("PK_JiraConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JiraProjectLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    JiraProjectKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    JiraProjectName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    JiraProjectUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    EpicKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Jql = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    DateWinner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreateIssuesForNewTasks = table.Column<bool>(type: "boolean", nullable: false),
                    IssueType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    WebhookSecretProtected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    LastErrorHint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    LastReconcileAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraProjectLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraProjectLinks_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JiraItemLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    IssueKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    WebUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    BaseTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    BaseAssigneeAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    BaseAssigneeEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    BaseWorkbenchDue = table.Column<DateOnly>(type: "date", nullable: true),
                    BaseJiraDue = table.Column<DateOnly>(type: "date", nullable: true),
                    BaseWorkbenchDone = table.Column<bool>(type: "boolean", nullable: false),
                    BaseJiraDone = table.Column<bool>(type: "boolean", nullable: false),
                    JiraStatus = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    EstimateHours = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    WorkbenchModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsRemoved = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraItemLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraItemLinks_JiraProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "JiraProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JiraSyncNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssueKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    Hint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsError = table.Column<bool>(type: "boolean", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraSyncNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraSyncNotes_JiraProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "JiraProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JiraWebhookReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    EventKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraWebhookReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraWebhookReceipts_JiraProjectLinks_LinkId",
                        column: x => x.LinkId,
                        principalTable: "JiraProjectLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JiraItemLinks_ItemId",
                table: "JiraItemLinks",
                column: "ItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraItemLinks_LinkId_IssueId",
                table: "JiraItemLinks",
                columns: new[] { "LinkId", "IssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraProjectLinks_ProjectId",
                table: "JiraProjectLinks",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraProjectLinks_SheetId",
                table: "JiraProjectLinks",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_JiraSyncNotes_ItemId_Field_IsOpen",
                table: "JiraSyncNotes",
                columns: new[] { "ItemId", "Field", "IsOpen" });

            migrationBuilder.CreateIndex(
                name: "IX_JiraSyncNotes_LinkId_IsOpen_CreatedAtUtc",
                table: "JiraSyncNotes",
                columns: new[] { "LinkId", "IsOpen", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JiraWebhookReceipts_LinkId_DedupKey",
                table: "JiraWebhookReceipts",
                columns: new[] { "LinkId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraWebhookReceipts_ReceivedAtUtc",
                table: "JiraWebhookReceipts",
                column: "ReceivedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JiraConnections");

            migrationBuilder.DropTable(
                name: "JiraItemLinks");

            migrationBuilder.DropTable(
                name: "JiraSyncNotes");

            migrationBuilder.DropTable(
                name: "JiraWebhookReceipts");

            migrationBuilder.DropTable(
                name: "JiraProjectLinks");
        }
    }
}
