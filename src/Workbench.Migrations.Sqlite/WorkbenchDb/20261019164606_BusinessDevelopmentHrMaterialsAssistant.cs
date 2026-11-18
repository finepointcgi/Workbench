using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class BusinessDevelopmentHrMaterialsAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBusinessDevelopment",
                table: "UserInvite",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHr",
                table: "UserInvite",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMaterials",
                table: "UserInvite",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPricing",
                table: "UserInvite",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBusinessDevelopment",
                table: "AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHr",
                table: "AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMaterials",
                table: "AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPricing",
                table: "AppUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssistantInteraction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PagePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    Answer = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OutcomeDetail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ToolCallCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantInteraction", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Connection = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MonthlyTokenLimit = table.Column<long>(type: "INTEGER", nullable: true),
                    MonthlyTokenLimitPerUser = table.Column<long>(type: "INTEGER", nullable: true),
                    DailyQuestionLimitPerUser = table.Column<int>(type: "INTEGER", nullable: true),
                    ExplainTimesheetFlags = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoticeAcceptedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    NoticeAcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NoticeVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectNarrative",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    CostVariance = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    ScheduleVariance = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    EacChanges = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Accomplishments = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    UpcomingMilestones = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Risks = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    DraftSource = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectNarrative", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectNarrative_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectNarrative_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimesheetFlagExplanation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    FactsHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimesheetFlagExplanation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantToolCall",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InteractionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Arguments = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RecordsRead = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CalledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantToolCall", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantToolCall_AssistantInteraction_InteractionId",
                        column: x => x.InteractionId,
                        principalTable: "AssistantInteraction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantInteraction_ConversationId",
                table: "AssistantInteraction",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantInteraction_StartedAtUtc",
                table: "AssistantInteraction",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantInteraction_UserEmail_StartedAtUtc",
                table: "AssistantInteraction",
                columns: new[] { "UserEmail", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantToolCall_InteractionId_Sequence",
                table: "AssistantToolCall",
                columns: new[] { "InteractionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNarrative_FiscalPeriodId",
                table: "ProjectNarrative",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNarrative_ProjectId_FiscalPeriodId",
                table: "ProjectNarrative",
                columns: new[] { "ProjectId", "FiscalPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetFlagExplanation_TimesheetId_RuleKey_FactsHash",
                table: "TimesheetFlagExplanation",
                columns: new[] { "TimesheetId", "RuleKey", "FactsHash" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantSettings");

            migrationBuilder.DropTable(
                name: "AssistantToolCall");

            migrationBuilder.DropTable(
                name: "ProjectNarrative");

            migrationBuilder.DropTable(
                name: "TimesheetFlagExplanation");

            migrationBuilder.DropTable(
                name: "AssistantInteraction");

            migrationBuilder.DropColumn(
                name: "IsBusinessDevelopment",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsHr",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsMaterials",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsPricing",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsBusinessDevelopment",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsHr",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsMaterials",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsPricing",
                table: "AppUser");
        }
    }
}
