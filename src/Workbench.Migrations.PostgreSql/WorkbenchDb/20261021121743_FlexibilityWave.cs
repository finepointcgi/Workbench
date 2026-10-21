using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class FlexibilityWave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomRoleIds",
                table: "UserInvite",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<int>(
                name: "Roles",
                table: "UserInvite",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CustomRoleIds",
                table: "SsoGroupMapping",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                collation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "SharedWith",
                table: "SavedReports",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true,
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "AssigneeRole",
                table: "ApprovalTask",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true,
                oldCollation: "C");

            migrationBuilder.AddColumn<string>(
                name: "CustomRoleIds",
                table: "ApiKeys",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                collation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "OrgRoles",
                table: "AccessReviewItem",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldCollation: "C");

            migrationBuilder.AddColumn<string>(
                name: "Permissions",
                table: "AccessReviewItem",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "RoleChanges",
                table: "AccessReviewItem",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "AutomationApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    TriggerDetail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ChangesJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    ParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    PrincipalOverride = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DedupKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationQueue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AutomationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    TriggerDetail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Principal = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    ParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    IsDryRun = table.Column<bool>(type: "boolean", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ConditionsJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    StepsJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Automations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TriggerKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    TriggerKeys = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    HasBlockingAction = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    TemplateKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Automations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PausedAll = table.Column<bool>(type: "boolean", nullable: false),
                    PausedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    PausedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxChainDepth = table.Column<int>(type: "integer", nullable: false),
                    MaxRunsPerRecordPerHour = table.Column<int>(type: "integer", nullable: false),
                    EventCursorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssigneeEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTriggerStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    FiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTriggerStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    HelpText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    FieldType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ReadOnlyAfterApproval = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSensitive = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    ValueListKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    LinkEntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormLayout",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    ScopeKey = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false, collation: "C"),
                    SectionsJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormLayout", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HomePagePreference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DashboardId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false, collation: "C"),
                    HiddenJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HomePagePreference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListViewPreference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ListKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ViewId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListViewPreference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberingSchemes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Pattern = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    PerCompany = table.Column<bool>(type: "boolean", nullable: false),
                    ResetEachYear = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberingSchemes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ScopeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    NextValue = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptDocument",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckIn = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckOut = table.Column<DateOnly>(type: "date", nullable: true),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ReadSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReadingJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true, collation: "C"),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstAttachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptDocument", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiptDocument_StoredFile_FileId",
                        column: x => x.FileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecordSubStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SetKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentValue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordSubStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegisteredPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Area = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    GrantedTo = table.Column<int>(type: "integer", nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredPermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleHomePage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    DashboardId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleHomePage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleHomePage_Dashboard_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, collation: "C"),
                    NormalizedName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    BuiltIn = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedListView",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    DefinitionJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedListView", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatusLabels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SetKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusLabels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SetKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ParentValue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValueLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValueLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationVersions_Automations_AutomationId",
                        column: x => x.AutomationId,
                        principalTable: "Automations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TextValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    NumberValue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    SearchText = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_CustomFieldDefinitions_FieldId",
                        column: x => x.FieldId,
                        principalTable: "CustomFieldDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeKind = table.Column<int>(type: "integer", nullable: false),
                    ScopeIds = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_AppUser_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ValueListItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValueListItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValueListItems_ValueLists_ValueListId",
                        column: x => x.ValueListId,
                        principalTable: "ValueLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "BuiltIn", "CreatedAtUtc", "CreatedBy", "Description", "Name", "NormalizedName", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[,]
                {
                    { new Guid("7b1e0000-0000-4000-8000-000000000001"), 1, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Sees everyone's timesheets and the Team time page.", "Timekeeper", "timekeeper", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000002"), 2, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Rates, contracts, budgets, accounting, billing, payables, payroll and project setup.", "Finance", "finance", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000004"), 4, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Everything, including users, roles and every workspace.", "Admin", "admin", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000008"), 8, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Staffs every project and sees capacity and utilization (no cost rates).", "Resource manager", "resource manager", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000010"), 16, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Runs the projects they manage: setup, budgets, staffing and approvals; creates projects.", "Project manager", "project manager", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000020"), 32, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Personnel records, EEO data, positions, compensation, benefits, onboarding and offboarding.", "HR", "hr", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000040"), 64, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Parts, inventory, BOMs, work orders, MRP, quality and shipping.", "Materials", "materials", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000080"), 128, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "The capture pipeline: leads, opportunities, accounts and forecast.", "Business development", "business development", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000000100"), 256, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Builds, compares and locks proposal prices.", "Pricing", "pricing", null, null },
                    { new Guid("7b1e0000-0000-4000-8000-000000001000"), 0, new DateTime(2026, 10, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "Everyone: records their own time, expenses, travel and leave, and sees what's shared with them. Held by every user.", "Employee", "employee", null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationApprovalRequests_ApprovalInstanceId",
                table: "AutomationApprovalRequests",
                column: "ApprovalInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationApprovalRequests_DocumentType_RecordId",
                table: "AutomationApprovalRequests",
                columns: new[] { "DocumentType", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationQueue_DedupKey",
                table: "AutomationQueue",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationQueue_ProcessedAtUtc_CreatedAtUtc",
                table: "AutomationQueue",
                columns: new[] { "ProcessedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_AutomationId_RecordId_StartedAtUtc",
                table: "AutomationRuns",
                columns: new[] { "AutomationId", "RecordId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_AutomationId_StartedAtUtc",
                table: "AutomationRuns",
                columns: new[] { "AutomationId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_StartedAtUtc",
                table: "AutomationRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Automations_IsEnabled_DeletedAtUtc",
                table: "Automations",
                columns: new[] { "IsEnabled", "DeletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTasks_AssigneeEmail_CompletedAtUtc",
                table: "AutomationTasks",
                columns: new[] { "AssigneeEmail", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTriggerStates_AutomationId_Key",
                table: "AutomationTriggerStates",
                columns: new[] { "AutomationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationVersions_AutomationId_Version",
                table: "AutomationVersions",
                columns: new[] { "AutomationId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldDefinitions_EntityType_Key",
                table: "CustomFieldDefinitions",
                columns: new[] { "EntityType", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_EntityType_RecordId",
                table: "CustomFieldValues",
                columns: new[] { "EntityType", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_FieldId_RecordId",
                table: "CustomFieldValues",
                columns: new[] { "FieldId", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_FieldId_SearchText",
                table: "CustomFieldValues",
                columns: new[] { "FieldId", "SearchText" });

            migrationBuilder.CreateIndex(
                name: "IX_FormLayout_EntityType_ScopeKey",
                table: "FormLayout",
                columns: new[] { "EntityType", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HomePagePreference_UserEmail_DashboardId",
                table: "HomePagePreference",
                columns: new[] { "UserEmail", "DashboardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListViewPreference_UserEmail_ListKey",
                table: "ListViewPreference",
                columns: new[] { "UserEmail", "ListKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberingSchemes_DocumentType",
                table: "NumberingSchemes",
                column: "DocumentType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequences_DocumentType_ScopeKey",
                table: "NumberSequences",
                columns: new[] { "DocumentType", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptDocument_FileId",
                table: "ReceiptDocument",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptDocument_OwnerEmail_CreatedAtUtc",
                table: "ReceiptDocument",
                columns: new[] { "OwnerEmail", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordSubStatuses_SetKey_RecordId",
                table: "RecordSubStatuses",
                columns: new[] { "SetKey", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPermissions_Key",
                table: "RegisteredPermissions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredPermissions_Source",
                table: "RegisteredPermissions",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_RoleHomePage_DashboardId",
                table: "RoleHomePage",
                column: "DashboardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleHomePage_RoleKey",
                table: "RoleHomePage",
                column: "RoleKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_Permission",
                table: "RolePermissions",
                column: "Permission");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleId_Permission",
                table: "RolePermissions",
                columns: new[] { "RoleId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_NormalizedName",
                table: "Roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedListView_ListKey",
                table: "SavedListView",
                column: "ListKey");

            migrationBuilder.CreateIndex(
                name: "IX_SavedListView_OwnerEmail",
                table: "SavedListView",
                column: "OwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_StatusLabels_SetKey_Value",
                table: "StatusLabels",
                columns: new[] { "SetKey", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubStatuses_SetKey_Code",
                table: "SubStatuses",
                columns: new[] { "SetKey", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ValueListItems_ValueListId_Code",
                table: "ValueListItems",
                columns: new[] { "ValueListId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ValueLists_Key",
                table: "ValueLists",
                column: "Key",
                unique: true);
            // Existing users' and invites' role flags become role assignments before the flag columns go.
            Workbench.Infrastructure.Persistence.CustomRolesMigration.ConvertOrgRoleFlags(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsBusinessDevelopment",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsFinance",
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
                name: "IsProjectManager",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsResourceManager",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsTimekeeper",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsBusinessDevelopment",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsFinance",
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

            migrationBuilder.DropColumn(
                name: "IsProjectManager",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsResourceManager",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "IsTimekeeper",
                table: "AppUser");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationApprovalRequests");

            migrationBuilder.DropTable(
                name: "AutomationQueue");

            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropTable(
                name: "AutomationSettings");

            migrationBuilder.DropTable(
                name: "AutomationTasks");

            migrationBuilder.DropTable(
                name: "AutomationTriggerStates");

            migrationBuilder.DropTable(
                name: "AutomationVersions");

            migrationBuilder.DropTable(
                name: "CustomFieldValues");

            migrationBuilder.DropTable(
                name: "FormLayout");

            migrationBuilder.DropTable(
                name: "HomePagePreference");

            migrationBuilder.DropTable(
                name: "ListViewPreference");

            migrationBuilder.DropTable(
                name: "NumberingSchemes");

            migrationBuilder.DropTable(
                name: "NumberSequences");

            migrationBuilder.DropTable(
                name: "ReceiptDocument");

            migrationBuilder.DropTable(
                name: "RecordSubStatuses");

            migrationBuilder.DropTable(
                name: "RegisteredPermissions");

            migrationBuilder.DropTable(
                name: "RoleHomePage");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "SavedListView");

            migrationBuilder.DropTable(
                name: "StatusLabels");

            migrationBuilder.DropTable(
                name: "SubStatuses");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "ValueListItems");

            migrationBuilder.DropTable(
                name: "Automations");

            migrationBuilder.DropTable(
                name: "CustomFieldDefinitions");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "ValueLists");

            migrationBuilder.DropColumn(
                name: "CustomRoleIds",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "Roles",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "CustomRoleIds",
                table: "SsoGroupMapping");

            migrationBuilder.DropColumn(
                name: "CustomRoleIds",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "AccessReviewItem");

            migrationBuilder.DropColumn(
                name: "RoleChanges",
                table: "AccessReviewItem");

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBusinessDevelopment",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFinance",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHr",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMaterials",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPricing",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsProjectManager",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsResourceManager",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTimekeeper",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "SharedWith",
                table: "SavedReports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true,
                oldCollation: "C");

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBusinessDevelopment",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFinance",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHr",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMaterials",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPricing",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsProjectManager",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsResourceManager",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTimekeeper",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "AssigneeRole",
                table: "ApprovalTask",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true,
                oldCollation: "C");

            migrationBuilder.AlterColumn<string>(
                name: "OrgRoles",
                table: "AccessReviewItem",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                collation: "C",
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldCollation: "C");
        }
    }
}
