using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessReview",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReview", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActivityEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApPaymentRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApPaymentRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUser",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    IsFinance = table.Column<bool>(type: "boolean", nullable: false),
                    IsTimekeeper = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSignInAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DeactivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeactivatedBy = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    SessionsRevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmployeeUnlinked = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ScimManaged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUser", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attachment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    ContentType = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false, collation: "C"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    UploadedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    BeforeJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    AfterJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Comment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Mentions = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpActuals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    WbsId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    FiscalPeriod = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false, collation: "C"),
                    Hours = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpActuals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpBudgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    WbsId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    FiscalPeriod = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false, collation: "C"),
                    Hours = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpBudgets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpEmployees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    LaborCategoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpEmployees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpLaborCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpLaborCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Start = table.Column<DateOnly>(type: "date", nullable: true),
                    End = table.Column<DateOnly>(type: "date", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpProjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpSyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Projects = table.Column<int>(type: "integer", nullable: false),
                    WbsElements = table.Column<int>(type: "integer", nullable: false),
                    Employees = table.Column<int>(type: "integer", nullable: false),
                    LaborCategories = table.Column<int>(type: "integer", nullable: false),
                    Budgets = table.Column<int>(type: "integer", nullable: false),
                    Actuals = table.Column<int>(type: "integer", nullable: false),
                    Inserted = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Deleted = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpSyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CpWbsElements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    WbsId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ParentWbsId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    ChargeAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CpWbsElements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dashboard",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboard", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FriendlyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    Xml = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FiscalYear",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalYear", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NormalBalance = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CostClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlAccounts_GlAccounts_ParentId",
                        column: x => x.ParentId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndirectRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Pool = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FiscalYear = table.Column<int>(type: "integer", nullable: false),
                    Basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupersededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SupersededById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndirectRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LaborCategory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborCategory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegalHold",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    PlacedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    PlacedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalHold", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    Link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationPreference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Delivery = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastDigestSentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgUnit_OrgUnit_ParentId",
                        column: x => x.ParentId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionPolicy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C"),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C"),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredFile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    UploadedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFile", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntryRevision",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldHours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    NewHours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    OldWbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    NewWbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    OldLaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    NewLaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TimesheetRevision = table.Column<int>(type: "integer", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntryRevision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserInvite",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    IsFinance = table.Column<bool>(type: "boolean", nullable: false),
                    IsTimekeeper = table.Column<bool>(type: "boolean", nullable: false),
                    InvitedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserInvite", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessReviewItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccessReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    OrgRoles = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    WorkspaceRoles = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    LastSignInAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmployeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    Dormant = table.Column<bool>(type: "boolean", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviewItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessReviewItem_AccessReview_AccessReviewId",
                        column: x => x.AccessReviewId,
                        principalTable: "AccessReview",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsPrime = table.Column<bool>(type: "boolean", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    CeilingValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FundedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contract", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contract_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerBillingProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentTermsDays = table.Column<int>(type: "integer", nullable: false),
                    BillingAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    StatementEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerBillingProfile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerBillingProfile_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Widget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DashboardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConfigJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Widget", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Widget_Dashboard_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FiscalPeriod",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalPeriod", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FiscalPeriod_FiscalYear_FiscalYearId",
                        column: x => x.FiscalYearId,
                        principalTable: "FiscalYear",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlPostingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlPostingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlPostingRules_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndirectPoolSetups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Pool = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Base = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CascadeOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CostClasses = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IncludeMappedIndirectLabor = table.Column<bool>(type: "boolean", nullable: false),
                    AppliedAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AbsorbedAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndirectPoolSetups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndirectPoolSetups_GlAccounts_AbsorbedAccountId",
                        column: x => x.AbsorbedAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndirectPoolSetups_GlAccounts_AppliedAccountId",
                        column: x => x.AppliedAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Employee",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupervisorId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultLaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TerminationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    StandardHoursPerWeek = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    IsExempt = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employee", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employee_Employee_SupervisorId",
                        column: x => x.SupervisorId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Employee_LaborCategory_DefaultLaborCategoryId",
                        column: x => x.DefaultLaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Employee_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Vendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Is1099 = table.Column<bool>(type: "boolean", nullable: false),
                    TaxIdLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true, collation: "C"),
                    Uei = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true, collation: "C"),
                    BusinessSize = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    PaymentTermsDays = table.Column<int>(type: "integer", nullable: false),
                    RemitToAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ContactEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SocioEconomicCategories = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ContactName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ContactPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true, collation: "C"),
                    Form1099Box = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    W9FileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vendor_StoredFile_W9FileId",
                        column: x => x.W9FileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sheets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    SchedulingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CostpointProjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sheets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMember",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceMember_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Clin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Pricing = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsOption = table.Column<bool>(type: "boolean", nullable: false),
                    IsExercised = table.Column<bool>(type: "boolean", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    CeilingValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FundedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clin", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clin_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractBillingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoicePrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    FeeWithholdRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    FeeWithholdCap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    MaterialHandlingRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "integer", nullable: true),
                    PayingOffice = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    AdminOffice = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CageCode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractBillingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractBillingSettings_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractLaborRate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    HourlyBillRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractLaborRate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractLaborRate_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractLaborRate_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractModification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractModification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractModification_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlJournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceRevision = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversalOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlJournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlJournalEntries_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlJournalEntries_GlJournalEntries_ReversalOfId",
                        column: x => x.ReversalOfId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UnbilledAccrual",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnbilledAccrual", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnbilledAccrual_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndirectPoolAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PoolSetupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndirectPoolAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndirectPoolAccounts_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndirectPoolAccounts_IndirectPoolSetups_PoolSetupId",
                        column: x => x.PoolSetupId,
                        principalTable: "IndirectPoolSetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeCostRate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    HourlyCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeCostRate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeCostRate_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseReport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Purpose = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    TripStart = table.Column<DateOnly>(type: "date", nullable: true),
                    TripEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RejectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseReport_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Timesheet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CertifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    FirstSubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CertificationText = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ReviewedBy = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    RejectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Timesheet", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Timesheet_Employee_ApprovedById",
                        column: x => x.ApprovedById,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Timesheet_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorTaxId",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    ProtectedValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorTaxId", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorTaxId_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Baseline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Baseline", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Baseline_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Columns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Options = table.Column<List<string>>(type: "text[]", nullable: false),
                    Formula = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Columns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Columns_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Start = table.Column<DateOnly>(type: "date", nullable: true),
                    Finish = table.Column<DateOnly>(type: "date", nullable: true),
                    CostpointWbsId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Values = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Items_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Project",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectManagerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: true),
                    Finish = table.Column<DateOnly>(type: "date", nullable: true),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Project", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Project_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Project_Employee_ProjectManagerId",
                        column: x => x.ProjectManagerId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Project_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Rule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    Conditions = table.Column<string>(type: "text", nullable: false),
                    Actions = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rule_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClinFundingNotice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThresholdPercent = table.Column<int>(type: "integer", nullable: false),
                    FundedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IncurredAndCommitted = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Recipients = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinFundingNotice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClinFundingNotice_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClinFundingNotice_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractModificationChange",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClinNumber = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    NewPeriodEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    ClinDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Pricing = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsOption = table.Column<bool>(type: "boolean", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    FeeRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractModificationChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractModificationChange_ContractModification_Modificatio~",
                        column: x => x.ModificationId,
                        principalTable: "ContractModification",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FundingAction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FundingAction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FundingAction_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FundingAction_ContractModification_ModificationId",
                        column: x => x.ModificationId,
                        principalTable: "ContractModification",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FundingAction_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApBill",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    InvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RetainageAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EnteredBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostingProblem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    FundingOverrideBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    FundingOverrideReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    DuplicateConfirmedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    AttachmentFileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApBill", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApBill_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBill_StoredFile_AttachmentFileId",
                        column: x => x.AttachmentFileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBill_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Invoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PaymentTermsDays = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Withheld = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AmountCredited = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditOfInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    RateNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    GenerationWarnings = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    FundingOverrideReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ReleasesWithhold = table.Column<bool>(type: "boolean", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoice_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_Invoice_CreditOfInvoiceId",
                        column: x => x.CreditOfInvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsVoid = table.Column<bool>(type: "boolean", nullable: false),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    VoidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payment_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payment_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeDayRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstEnteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeDayRecord", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeDayRecord_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeDayRecord_Timesheet_TimesheetId",
                        column: x => x.TimesheetId,
                        principalTable: "Timesheet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BaselineEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: true),
                    Finish = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaselineEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BaselineEntry_Baseline_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "Baseline",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BaselineEntry_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dependency",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredecessorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuccessorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LagDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dependency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dependency_Items_PredecessorId",
                        column: x => x.PredecessorId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dependency_Items_SuccessorId",
                        column: x => x.SuccessorId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dependency_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BudgetVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LockedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BudgetVersion_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subcontract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrimeContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Pricing = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    CeilingValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FundedValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RetainagePercent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subcontract", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subcontract_Contract_PrimeContractId",
                        column: x => x.PrimeContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subcontract_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subcontract_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WbsElement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsChargeable = table.Column<bool>(type: "boolean", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresAssignment = table.Column<bool>(type: "boolean", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WbsElement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WbsElement_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WbsElement_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WbsElement_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WbsElement_WbsElement_ParentId",
                        column: x => x.ParentId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuleRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    FiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    TriggerDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleRun_Rule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "Rule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApPayment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaidBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    VoidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApPayment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApPayment_ApBill_BillId",
                        column: x => x.BillId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApPayment_ApPaymentRun_RunId",
                        column: x => x.RunId,
                        principalTable: "ApPaymentRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApPayment_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApPayment_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillingMilestone",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingMilestone", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingMilestone_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingMilestone_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingMilestone_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentApplication",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentApplication", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentApplication_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentApplication_Payment_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubcontractFlowDown",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClauseNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    IsIncluded = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontractFlowDown", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubcontractFlowDown_Subcontract_SubcontractId",
                        column: x => x.SubcontractId,
                        principalTable: "Subcontract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubcontractFundingAction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FundedChange = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CeilingChange = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontractFundingAction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubcontractFundingAction_Subcontract_SubcontractId",
                        column: x => x.SubcontractId,
                        principalTable: "Subcontract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BudgetLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    CostType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    OdcType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    PlanningRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BudgetLine_BudgetVersion_BudgetVersionId",
                        column: x => x.BudgetVersionId,
                        principalTable: "BudgetVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BudgetLine_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BudgetLine_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChargeAssignment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    From = table.Column<DateOnly>(type: "date", nullable: true),
                    To = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChargeAssignment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChargeAssignment_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChargeAssignment_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GlJournalLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    Debit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlJournalLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlJournalLines_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlJournalLines_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlJournalLines_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlJournalLines_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlJournalLines_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlWbsCostPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pool = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlWbsCostPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlWbsCostPools_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    OdcType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    Quantity = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsGenerated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OdcEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IncurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Vendor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcontractId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsUnallowable = table.Column<bool>(type: "boolean", nullable: false),
                    UnallowableReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ExpenseReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceiptAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    RejectedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RejectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OdcEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OdcEntry_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OdcEntry_ExpenseReport_ExpenseReportId",
                        column: x => x.ExpenseReportId,
                        principalTable: "ExpenseReport",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OdcEntry_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OdcEntry_Subcontract_SubcontractId",
                        column: x => x.SubcontractId,
                        principalTable: "Subcontract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OdcEntry_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OdcEntry_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimesheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    EnteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeEntry_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntry_Timesheet_TimesheetId",
                        column: x => x.TimesheetId,
                        principalTable: "Timesheet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TimeEntry_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BilledSource",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    LaborCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BilledSource", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BilledSource_InvoiceLine_InvoiceLineId",
                        column: x => x.InvoiceLineId,
                        principalTable: "InvoiceLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApBillLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    OdcType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SubcontractId = table.Column<Guid>(type: "uuid", nullable: true),
                    OdcEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetainageAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApproverEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApBillLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApBillLine_ApBill_BillId",
                        column: x => x.BillId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApBillLine_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillLine_OdcEntry_OdcEntryId",
                        column: x => x.OdcEntryId,
                        principalTable: "OdcEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillLine_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillLine_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillLine_Subcontract_SubcontractId",
                        column: x => x.SubcontractId,
                        principalTable: "Subcontract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillLine_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItem_AccessReviewId_UserEmail",
                table: "AccessReviewItem",
                columns: new[] { "AccessReviewId", "UserEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvent_SheetId_ItemId_OccurredAtUtc",
                table: "ActivityEvent",
                columns: new[] { "SheetId", "ItemId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvent_SheetId_OccurredAtUtc",
                table: "ActivityEvent",
                columns: new[] { "SheetId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_AttachmentFileId",
                table: "ApBill",
                column: "AttachmentFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_JournalEntryId",
                table: "ApBill",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_Number",
                table: "ApBill",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_Status_DueDate",
                table: "ApBill",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_VendorId_InvoiceNumber",
                table: "ApBill",
                columns: new[] { "VendorId", "InvoiceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_AccountId",
                table: "ApBillLine",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_BillId",
                table: "ApBillLine",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_OdcEntryId",
                table: "ApBillLine",
                column: "OdcEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_OrgUnitId",
                table: "ApBillLine",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_ProjectId",
                table: "ApBillLine",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_SubcontractId",
                table: "ApBillLine",
                column: "SubcontractId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_WbsElementId",
                table: "ApBillLine",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_ApPayment_BillId",
                table: "ApPayment",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_ApPayment_JournalEntryId",
                table: "ApPayment",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApPayment_RunId",
                table: "ApPayment",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ApPayment_VendorId_PaidOn",
                table: "ApPayment",
                columns: new[] { "VendorId", "PaidOn" });

            migrationBuilder.CreateIndex(
                name: "IX_ApPaymentRun_Number",
                table: "ApPaymentRun",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUser_Email",
                table: "AppUser",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachment_SheetId_ItemId",
                table: "Attachment",
                columns: new[] { "SheetId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_EntityType_EntityId",
                table: "AuditEvent",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_OccurredAtUtc",
                table: "AuditEvent",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Baseline_SheetId_CapturedAtUtc",
                table: "Baseline",
                columns: new[] { "SheetId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BaselineEntry_BaselineId_ItemId",
                table: "BaselineEntry",
                columns: new[] { "BaselineId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BaselineEntry_ItemId",
                table: "BaselineEntry",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_BilledSource_InvoiceId",
                table: "BilledSource",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BilledSource_InvoiceLineId",
                table: "BilledSource",
                column: "InvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_BilledSource_Kind_SourceId",
                table: "BilledSource",
                columns: new[] { "Kind", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingMilestone_ClinId",
                table: "BillingMilestone",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingMilestone_ContractId_ClinId",
                table: "BillingMilestone",
                columns: new[] { "ContractId", "ClinId" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingMilestone_InvoiceId",
                table: "BillingMilestone",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetLine_BudgetVersionId_WbsElementId",
                table: "BudgetLine",
                columns: new[] { "BudgetVersionId", "WbsElementId" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetLine_LaborCategoryId",
                table: "BudgetLine",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetLine_WbsElementId",
                table: "BudgetLine",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetVersion_ProjectId_Kind",
                table: "BudgetVersion",
                columns: new[] { "ProjectId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeAssignment_EmployeeId",
                table: "ChargeAssignment",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeAssignment_LaborCategoryId",
                table: "ChargeAssignment",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeAssignment_WbsElementId_EmployeeId",
                table: "ChargeAssignment",
                columns: new[] { "WbsElementId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Clin_ContractId_Number",
                table: "Clin",
                columns: new[] { "ContractId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinFundingNotice_ClinId_ThresholdPercent_FundedValue",
                table: "ClinFundingNotice",
                columns: new[] { "ClinId", "ThresholdPercent", "FundedValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinFundingNotice_ContractId",
                table: "ClinFundingNotice",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Columns_SheetId",
                table: "Columns",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_Comment_SheetId_ItemId_CreatedAtUtc",
                table: "Comment",
                columns: new[] { "SheetId", "ItemId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_ContractNumber",
                table: "Contract",
                column: "ContractNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contract_CustomerId",
                table: "Contract",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractBillingSettings_ContractId",
                table: "ContractBillingSettings",
                column: "ContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractLaborRate_ContractId_LaborCategoryId_EffectiveFrom",
                table: "ContractLaborRate",
                columns: new[] { "ContractId", "LaborCategoryId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractLaborRate_LaborCategoryId",
                table: "ContractLaborRate",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractModification_ContractId_Number",
                table: "ContractModification",
                columns: new[] { "ContractId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractModificationChange_ModificationId_Sequence",
                table: "ContractModificationChange",
                columns: new[] { "ModificationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_CpActuals_ProjectId_WbsId_FiscalPeriod",
                table: "CpActuals",
                columns: new[] { "ProjectId", "WbsId", "FiscalPeriod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CpBudgets_ProjectId_WbsId_FiscalPeriod",
                table: "CpBudgets",
                columns: new[] { "ProjectId", "WbsId", "FiscalPeriod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CpEmployees_EmployeeId",
                table: "CpEmployees",
                column: "EmployeeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CpLaborCategories_LaborCategoryId",
                table: "CpLaborCategories",
                column: "LaborCategoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CpProjects_ProjectId",
                table: "CpProjects",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CpSyncRuns_StartedAtUtc",
                table: "CpSyncRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CpWbsElements_ProjectId_WbsId",
                table: "CpWbsElements",
                columns: new[] { "ProjectId", "WbsId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customer_Code",
                table: "Customer",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBillingProfile_CustomerId",
                table: "CustomerBillingProfile",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dashboard_OwnerEmail",
                table: "Dashboard",
                column: "OwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Dependency_PredecessorId_SuccessorId",
                table: "Dependency",
                columns: new[] { "PredecessorId", "SuccessorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dependency_SheetId",
                table: "Dependency",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_Dependency_SuccessorId",
                table: "Dependency",
                column: "SuccessorId");

            migrationBuilder.CreateIndex(
                name: "IX_Employee_DefaultLaborCategoryId",
                table: "Employee",
                column: "DefaultLaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Employee_Email",
                table: "Employee",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employee_EmployeeNumber",
                table: "Employee",
                column: "EmployeeNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employee_OrgUnitId",
                table: "Employee",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Employee_SupervisorId",
                table: "Employee",
                column: "SupervisorId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeCostRate_EmployeeId_EffectiveFrom",
                table: "EmployeeCostRate",
                columns: new[] { "EmployeeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseReport_EmployeeId_Status",
                table: "ExpenseReport",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriod_FiscalYearId",
                table: "FiscalPeriod",
                column: "FiscalYearId");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriod_Name",
                table: "FiscalPeriod",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalPeriod_Start",
                table: "FiscalPeriod",
                column: "Start");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYear_Year",
                table: "FiscalYear",
                column: "Year",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FundingAction_ClinId_EffectiveDate",
                table: "FundingAction",
                columns: new[] { "ClinId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_FundingAction_ContractId",
                table: "FundingAction",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_FundingAction_ModificationId",
                table: "FundingAction",
                column: "ModificationId");

            migrationBuilder.CreateIndex(
                name: "IX_GlAccounts_Number",
                table: "GlAccounts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlAccounts_ParentId",
                table: "GlAccounts",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalEntries_Date",
                table: "GlJournalEntries",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalEntries_FiscalPeriodId_Status",
                table: "GlJournalEntries",
                columns: new[] { "FiscalPeriodId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalEntries_ReversalOfId",
                table: "GlJournalEntries",
                column: "ReversalOfId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalEntries_Sequence",
                table: "GlJournalEntries",
                column: "Sequence",
                unique: true,
                filter: "\"Sequence\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalEntries_Source_SourceId_SourceRevision_FiscalPerio~",
                table: "GlJournalEntries",
                columns: new[] { "Source", "SourceId", "SourceRevision", "FiscalPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_AccountId",
                table: "GlJournalLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_JournalEntryId",
                table: "GlJournalLines",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_OrgUnitId",
                table: "GlJournalLines",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_ProjectId_WbsElementId",
                table: "GlJournalLines",
                columns: new[] { "ProjectId", "WbsElementId" });

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_WbsElementId",
                table: "GlJournalLines",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_GlPostingRules_AccountId",
                table: "GlPostingRules",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GlPostingRules_Kind_Key",
                table: "GlPostingRules",
                columns: new[] { "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlWbsCostPools_WbsElementId",
                table: "GlWbsCostPools",
                column: "WbsElementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndirectPoolAccounts_AccountId",
                table: "IndirectPoolAccounts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_IndirectPoolAccounts_PoolSetupId_AccountId",
                table: "IndirectPoolAccounts",
                columns: new[] { "PoolSetupId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndirectPoolSetups_AbsorbedAccountId",
                table: "IndirectPoolSetups",
                column: "AbsorbedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_IndirectPoolSetups_AppliedAccountId",
                table: "IndirectPoolSetups",
                column: "AppliedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_IndirectPoolSetups_Pool",
                table: "IndirectPoolSetups",
                column: "Pool",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndirectRates_FiscalYear_Basis_Pool",
                table: "IndirectRates",
                columns: new[] { "FiscalYear", "Basis", "Pool" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_ContractId_Sequence",
                table: "Invoice",
                columns: new[] { "ContractId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_CreditOfInvoiceId",
                table: "Invoice",
                column: "CreditOfInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_CustomerId_Status",
                table: "Invoice",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_JournalEntryId",
                table: "Invoice",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_Number",
                table: "Invoice",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_ClinId",
                table: "InvoiceLine",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_InvoiceId_LineNumber",
                table: "InvoiceLine",
                columns: new[] { "InvoiceId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_LaborCategoryId",
                table: "InvoiceLine",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_ProjectId",
                table: "InvoiceLine",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_WbsElementId",
                table: "InvoiceLine",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_SheetId_ParentId_Order",
                table: "Items",
                columns: new[] { "SheetId", "ParentId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_LaborCategory_Code",
                table: "LaborCategory",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalHold_EntityType_EntityId",
                table: "LegalHold",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_RecipientEmail_CreatedAtUtc",
                table: "Notification",
                columns: new[] { "RecipientEmail", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_RecipientEmail_ReadAtUtc",
                table: "Notification",
                columns: new[] { "RecipientEmail", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreference_UserEmail",
                table: "NotificationPreference",
                column: "UserEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_EmployeeId",
                table: "OdcEntry",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_ExpenseReportId",
                table: "OdcEntry",
                column: "ExpenseReportId");

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_ProjectId_IncurredOn",
                table: "OdcEntry",
                columns: new[] { "ProjectId", "IncurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_Status",
                table: "OdcEntry",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_SubcontractId",
                table: "OdcEntry",
                column: "SubcontractId");

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_VendorId",
                table: "OdcEntry",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_OdcEntry_WbsElementId",
                table: "OdcEntry",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnit_Code",
                table: "OrgUnit",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnit_ParentId",
                table: "OrgUnit",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_CustomerId_ReceivedOn",
                table: "Payment",
                columns: new[] { "CustomerId", "ReceivedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_Payment_JournalEntryId",
                table: "Payment",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentApplication_InvoiceId",
                table: "PaymentApplication",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentApplication_PaymentId",
                table: "PaymentApplication",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Project_Code",
                table: "Project",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Project_ContractId",
                table: "Project",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Project_ProjectManagerId",
                table: "Project",
                column: "ProjectManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_Project_SheetId",
                table: "Project",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionPolicy_RecordType",
                table: "RetentionPolicy",
                column: "RecordType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rule_SheetId_Enabled",
                table: "Rule",
                columns: new[] { "SheetId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleRun_RuleId_ItemId_TriggerDate",
                table: "RuleRun",
                columns: new[] { "RuleId", "ItemId", "TriggerDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleRun_SheetId_FiredAtUtc",
                table: "RuleRun",
                columns: new[] { "SheetId", "FiredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_Actor_OccurredAtUtc",
                table: "SecurityEvent",
                columns: new[] { "Actor", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_EventType_OccurredAtUtc",
                table: "SecurityEvent",
                columns: new[] { "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvent_OccurredAtUtc",
                table: "SecurityEvent",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Sheets_UpdatedAtUtc",
                table: "Sheets",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Sheets_WorkspaceId",
                table: "Sheets",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFile_OwnerType_OwnerId",
                table: "StoredFile",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Subcontract_Number",
                table: "Subcontract",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontract_PrimeContractId",
                table: "Subcontract",
                column: "PrimeContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Subcontract_ProjectId",
                table: "Subcontract",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Subcontract_VendorId",
                table: "Subcontract",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontractFlowDown_SubcontractId_ClauseNumber",
                table: "SubcontractFlowDown",
                columns: new[] { "SubcontractId", "ClauseNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontractFundingAction_SubcontractId_EffectiveDate",
                table: "SubcontractFundingAction",
                columns: new[] { "SubcontractId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeDayRecord_EmployeeId_WorkDate",
                table: "TimeDayRecord",
                columns: new[] { "EmployeeId", "WorkDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeDayRecord_TimesheetId",
                table: "TimeDayRecord",
                column: "TimesheetId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntry_LaborCategoryId",
                table: "TimeEntry",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntry_TimesheetId",
                table: "TimeEntry",
                column: "TimesheetId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntry_WbsElementId_WorkDate",
                table: "TimeEntry",
                columns: new[] { "WbsElementId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntryRevision_TimeEntryId",
                table: "TimeEntryRevision",
                column: "TimeEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntryRevision_TimesheetId",
                table: "TimeEntryRevision",
                column: "TimesheetId");

            migrationBuilder.CreateIndex(
                name: "IX_Timesheet_ApprovedById",
                table: "Timesheet",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_Timesheet_EmployeeId_PeriodStart",
                table: "Timesheet",
                columns: new[] { "EmployeeId", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Timesheet_Status",
                table: "Timesheet",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UnbilledAccrual_FiscalPeriodId",
                table: "UnbilledAccrual",
                column: "FiscalPeriodId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserInvite_Email",
                table: "UserInvite",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Vendor_Code",
                table: "Vendor",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vendor_Uei",
                table: "Vendor",
                column: "Uei");

            migrationBuilder.CreateIndex(
                name: "IX_Vendor_W9FileId",
                table: "Vendor",
                column: "W9FileId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorTaxId_VendorId",
                table: "VendorTaxId",
                column: "VendorId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WbsElement_ClinId",
                table: "WbsElement",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_WbsElement_Code",
                table: "WbsElement",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WbsElement_ItemId",
                table: "WbsElement",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WbsElement_ParentId",
                table: "WbsElement",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_WbsElement_ProjectId_ParentId_Order",
                table: "WbsElement",
                columns: new[] { "ProjectId", "ParentId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Widget_DashboardId_Order",
                table: "Widget",
                columns: new[] { "DashboardId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMember_UserEmail",
                table: "WorkspaceMember",
                column: "UserEmail");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMember_WorkspaceId_UserEmail",
                table: "WorkspaceMember",
                columns: new[] { "WorkspaceId", "UserEmail" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessReviewItem");

            migrationBuilder.DropTable(
                name: "ActivityEvent");

            migrationBuilder.DropTable(
                name: "ApBillLine");

            migrationBuilder.DropTable(
                name: "ApPayment");

            migrationBuilder.DropTable(
                name: "AppUser");

            migrationBuilder.DropTable(
                name: "Attachment");

            migrationBuilder.DropTable(
                name: "AuditEvent");

            migrationBuilder.DropTable(
                name: "BaselineEntry");

            migrationBuilder.DropTable(
                name: "BilledSource");

            migrationBuilder.DropTable(
                name: "BillingMilestone");

            migrationBuilder.DropTable(
                name: "BudgetLine");

            migrationBuilder.DropTable(
                name: "ChargeAssignment");

            migrationBuilder.DropTable(
                name: "ClinFundingNotice");

            migrationBuilder.DropTable(
                name: "Columns");

            migrationBuilder.DropTable(
                name: "Comment");

            migrationBuilder.DropTable(
                name: "ContractBillingSettings");

            migrationBuilder.DropTable(
                name: "ContractLaborRate");

            migrationBuilder.DropTable(
                name: "ContractModificationChange");

            migrationBuilder.DropTable(
                name: "CpActuals");

            migrationBuilder.DropTable(
                name: "CpBudgets");

            migrationBuilder.DropTable(
                name: "CpEmployees");

            migrationBuilder.DropTable(
                name: "CpLaborCategories");

            migrationBuilder.DropTable(
                name: "CpProjects");

            migrationBuilder.DropTable(
                name: "CpSyncRuns");

            migrationBuilder.DropTable(
                name: "CpWbsElements");

            migrationBuilder.DropTable(
                name: "CustomerBillingProfile");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "Dependency");

            migrationBuilder.DropTable(
                name: "EmployeeCostRate");

            migrationBuilder.DropTable(
                name: "FundingAction");

            migrationBuilder.DropTable(
                name: "GlJournalLines");

            migrationBuilder.DropTable(
                name: "GlPostingRules");

            migrationBuilder.DropTable(
                name: "GlWbsCostPools");

            migrationBuilder.DropTable(
                name: "IndirectPoolAccounts");

            migrationBuilder.DropTable(
                name: "IndirectRates");

            migrationBuilder.DropTable(
                name: "LegalHold");

            migrationBuilder.DropTable(
                name: "Notification");

            migrationBuilder.DropTable(
                name: "NotificationPreference");

            migrationBuilder.DropTable(
                name: "PaymentApplication");

            migrationBuilder.DropTable(
                name: "RetentionPolicy");

            migrationBuilder.DropTable(
                name: "RuleRun");

            migrationBuilder.DropTable(
                name: "SecurityEvent");

            migrationBuilder.DropTable(
                name: "SubcontractFlowDown");

            migrationBuilder.DropTable(
                name: "SubcontractFundingAction");

            migrationBuilder.DropTable(
                name: "TimeDayRecord");

            migrationBuilder.DropTable(
                name: "TimeEntry");

            migrationBuilder.DropTable(
                name: "TimeEntryRevision");

            migrationBuilder.DropTable(
                name: "UnbilledAccrual");

            migrationBuilder.DropTable(
                name: "UserInvite");

            migrationBuilder.DropTable(
                name: "VendorTaxId");

            migrationBuilder.DropTable(
                name: "Widget");

            migrationBuilder.DropTable(
                name: "WorkspaceMember");

            migrationBuilder.DropTable(
                name: "AccessReview");

            migrationBuilder.DropTable(
                name: "OdcEntry");

            migrationBuilder.DropTable(
                name: "ApBill");

            migrationBuilder.DropTable(
                name: "ApPaymentRun");

            migrationBuilder.DropTable(
                name: "Baseline");

            migrationBuilder.DropTable(
                name: "InvoiceLine");

            migrationBuilder.DropTable(
                name: "BudgetVersion");

            migrationBuilder.DropTable(
                name: "ContractModification");

            migrationBuilder.DropTable(
                name: "IndirectPoolSetups");

            migrationBuilder.DropTable(
                name: "Payment");

            migrationBuilder.DropTable(
                name: "Rule");

            migrationBuilder.DropTable(
                name: "Timesheet");

            migrationBuilder.DropTable(
                name: "Dashboard");

            migrationBuilder.DropTable(
                name: "ExpenseReport");

            migrationBuilder.DropTable(
                name: "Subcontract");

            migrationBuilder.DropTable(
                name: "Invoice");

            migrationBuilder.DropTable(
                name: "WbsElement");

            migrationBuilder.DropTable(
                name: "GlAccounts");

            migrationBuilder.DropTable(
                name: "Vendor");

            migrationBuilder.DropTable(
                name: "GlJournalEntries");

            migrationBuilder.DropTable(
                name: "Clin");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "Project");

            migrationBuilder.DropTable(
                name: "StoredFile");

            migrationBuilder.DropTable(
                name: "FiscalPeriod");

            migrationBuilder.DropTable(
                name: "Contract");

            migrationBuilder.DropTable(
                name: "Employee");

            migrationBuilder.DropTable(
                name: "Sheets");

            migrationBuilder.DropTable(
                name: "FiscalYear");

            migrationBuilder.DropTable(
                name: "Customer");

            migrationBuilder.DropTable(
                name: "LaborCategory");

            migrationBuilder.DropTable(
                name: "OrgUnit");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
