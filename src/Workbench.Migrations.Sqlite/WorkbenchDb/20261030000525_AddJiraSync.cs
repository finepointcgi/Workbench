using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Flavor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    TokenProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    TokenHint = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConnectedAsName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ConnectedAsAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
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
                    table.PrimaryKey("PK_JiraConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JiraProjectLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JiraProjectKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    JiraProjectName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    JiraProjectUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    EpicKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Jql = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DateWinner = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreateIssuesForNewTasks = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WebhookSecretProtected = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastErrorHint = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastReconcileAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IssueKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WebUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    BaseTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BaseAssigneeAccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BaseAssigneeEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    BaseWorkbenchDue = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    BaseJiraDue = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    BaseWorkbenchDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    BaseJiraDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    JiraStatus = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EstimateHours = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: true),
                    WorkbenchModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsRemoved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IssueKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Field = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Hint = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsError = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
