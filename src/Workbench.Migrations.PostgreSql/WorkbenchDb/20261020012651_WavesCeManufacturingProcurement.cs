using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class WavesCeManufacturingProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "InventoryTransaction",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "InventoryBalance",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddColumn<string>(
                name: "DisposalKind",
                table: "FixedAsset",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FromCompanyId",
                table: "AssetTransfer",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FromContractId",
                table: "AssetTransfer",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "JournalEntryId",
                table: "AssetTransfer",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ToCompanyId",
                table: "AssetTransfer",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ToContractId",
                table: "AssetTransfer",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Roles = table.Column<int>(type: "integer", nullable: false),
                    Scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    ServiceUserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RevokedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedFrom = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssetTaxBooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Book = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RecoveryYears = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: false),
                    Convention = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Basis = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PlacedInServiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Section179Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BonusPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    DisposalDepreciation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DisposalAdjustedBasis = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DisposalGainLoss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTaxBooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetTaxBooks_FixedAsset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "FixedAsset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackflushSetting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    BomId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsBackflushed = table.Column<bool>(type: "boolean", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    BinId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackflushSetting", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackflushSetting_BillOfMaterial_BomId",
                        column: x => x.BomId,
                        principalTable: "BillOfMaterial",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackflushSetting_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackflushSetting_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackflushSetting_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackflushShortage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityShort = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackflushShortage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackflushShortage_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackflushShortage_WorkOrderComponent_WorkOrderComponentId",
                        column: x => x.WorkOrderComponentId,
                        principalTable: "WorkOrderComponent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackflushShortage_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankAchSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImmediateDestination = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false, collation: "C"),
                    ImmediateDestinationName = table.Column<string>(type: "character varying(23)", maxLength: 23, nullable: false, collation: "C"),
                    ImmediateOrigin = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    ImmediateOriginName = table.Column<string>(type: "character varying(23)", maxLength: 23, nullable: false, collation: "C"),
                    CompanyIdentification = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    CompanyName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    EntryDescription = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    OriginatingDfi = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    BalancedFile = table.Column<bool>(type: "boolean", nullable: false),
                    PositivePayLayoutId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAchSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankAchSettings_BankAccounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "BankAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CapacityScheduleRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Finite = table.Column<bool>(type: "boolean", nullable: false),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    OperationCount = table.Column<int>(type: "integer", nullable: false),
                    LateCount = table.Column<int>(type: "integer", nullable: false),
                    Messages = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    RunBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    RunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapacityScheduleRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DisclosureStatements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Form = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    PreviousVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeSummary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MadeEffectiveBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    MadeEffectiveAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisclosureStatements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DisclosureStatements_DisclosureStatements_PreviousVersionId",
                        column: x => x.PreviousVersionId,
                        principalTable: "DisclosureStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EdiPartner",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    InterchangeQualifier = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, collation: "C"),
                    InterchangeId = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false, collation: "C"),
                    ApplicationCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true, collation: "C"),
                    Transport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    SendPurchaseOrders = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    NextControlNumber = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EdiPartner", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EdiPartner_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EngineeringChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    EffectivityKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveSerialOrLot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    StockDisposition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WorkOrderDisposition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CostImpact = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    EffectiveAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineeringChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationConnectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SettingsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, collation: "C"),
                    SecretsProtected = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    EventTypes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Schedule = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RunAtHourUtc = table.Column<int>(type: "integer", nullable: false),
                    RunOnDay = table.Column<int>(type: "integer", nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationConnectors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationOutboxEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveryCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationOutboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Kit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    StagingWarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    StagingBinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NeededBy = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Kit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Kit_Bin_StagingBinId",
                        column: x => x.StagingBinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Kit_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Kit_Warehouse_StagingWarehouseId",
                        column: x => x.StagingWarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Kit_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Kit_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LandedCharge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApBillLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreditAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LandedCharge", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LandedCharge_ApBillLine_ApBillLineId",
                        column: x => x.ApBillLineId,
                        principalTable: "ApBillLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LandedCharge_GlAccounts_CreditAccountId",
                        column: x => x.CreditAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LandedCharge_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LandedCharge_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationSchedule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkCenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    QueuePosition = table.Column<int>(type: "integer", nullable: false),
                    PinnedStart = table.Column<DateOnly>(type: "date", nullable: true),
                    Start = table.Column<DateOnly>(type: "date", nullable: true),
                    End = table.Column<DateOnly>(type: "date", nullable: true),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    IsLate = table.Column<bool>(type: "boolean", nullable: false),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ScheduledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationSchedule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationSchedule_WorkCenter_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationSchedule_WorkOrderOperation_WorkOrderOperationId",
                        column: x => x.WorkOrderOperationId,
                        principalTable: "WorkOrderOperation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OperationSchedule_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentFileDownloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false, collation: "C"),
                    FileIdModifier = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: true, collation: "C"),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DownloadedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DownloadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentFileDownloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentFileDownloads_ApPaymentRun_RunId",
                        column: x => x.RunId,
                        principalTable: "ApPaymentRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentFileDownloads_BankAccounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "BankAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhysicalInventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    WholeWarehouse = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CountDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecountPercent = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    RecountValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BlindCount = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    FrozenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalInventory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhysicalInventory_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhysicalInventory_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PositivePayLayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    FieldsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, collation: "C"),
                    IncludeHeader = table.Column<bool>(type: "boolean", nullable: false),
                    IssueCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    VoidCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositivePayLayouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcurementFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcontractId = table.Column<Guid>(type: "uuid", nullable: true),
                    Competition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QuotesSolicited = table.Column<int>(type: "integer", nullable: true),
                    QuotesReceived = table.Column<int>(type: "integer", nullable: true),
                    SoleSourceJustification = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    PriceAnalysis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PriceAnalysisNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    FlowdownClauses = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    DebarmentCheckedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DebarmentResult = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    SmallBusinessReportPeriod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcurementFiles_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcurementFiles_Subcontract_SubcontractId",
                        column: x => x.SubcontractId,
                        principalTable: "Subcontract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderAcknowledgement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderAcknowledgement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderAcknowledgement_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderAcknowledgement_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    UploadedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityAttachments_StoredFile_FileId",
                        column: x => x.FileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityDefectCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityDefectCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspectionPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OperationSequence = table.Column<int>(type: "integer", nullable: true),
                    Sampling = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    AcceptNumber = table.Column<int>(type: "integer", nullable: false),
                    Aql = table.Column<decimal>(type: "numeric(9,3)", precision: 9, scale: 3, nullable: true),
                    RequiredDocuments = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspectionPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityInspectionPlans_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RemittanceAdvices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Payments = table.Column<int>(type: "integer", nullable: false),
                    SentBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemittanceAdvices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemittanceAdvices_ApPaymentRun_RunId",
                        column: x => x.RunId,
                        principalTable: "ApPaymentRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RemittanceAdvices_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Rfq",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Terms = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    ShipTo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    Instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IssuedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rfq", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrder",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-00000000c001")),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OnCreditHold = table.Column<bool>(type: "boolean", nullable: false),
                    OnHold = table.Column<bool>(type: "boolean", nullable: false),
                    HoldReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ShipToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ShipToAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ShipToCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    MarkForCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    IssuedByCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    InspectByCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    AcceptByCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    InspectionPoint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AcceptancePoint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrder_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrder_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrder_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrder_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrder_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShipNotice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ShipDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpectedDelivery = table.Column<DateOnly>(type: "date", nullable: true),
                    Carrier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    TrackingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipNotice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipNotice_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipNotice_PurchaseReceipt_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "PurchaseReceipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipNotice_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierContact",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    FailedSignIns = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSignInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SessionVersion = table.Column<int>(type: "integer", nullable: false),
                    InvitedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeactivatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeactivatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierContact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierContact_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StatedTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvoice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierInvoice_ApBill_BillId",
                        column: x => x.BillId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierInvoice_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierInvoice_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPortalEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Target = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPortalEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Treatment = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxExemptionCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    State = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    FileId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxExemptionCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxExemptionCertificates_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxJurisdictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    State = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, collation: "C"),
                    County = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxJurisdictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorBankAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountHolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ProtectedRoutingNumber = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    ProtectedAccountNumber = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    RoutingLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    AccountLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    RemittanceEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorBankAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorBankAccounts_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorBankChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AccountHolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ProtectedRoutingNumber = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    ProtectedAccountNumber = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    RoutingLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    AccountLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    RemittanceEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    RequestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorBankChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorBankChanges_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    EventTypes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SecretProtected = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SecretHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    SecretRotatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Health = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    UnhealthySinceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextProbeAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailureAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkCenterCalendar",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkCenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkingDays = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false, collation: "C"),
                    HoursPerDay = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Units = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCenterCalendar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkCenterCalendar_WorkCenter_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkCenterCapacityException",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkCenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCenterCapacityException", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkCenterCapacityException_WorkCenter_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DisclosureAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Choice = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisclosureAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DisclosureAnswers_DisclosureStatements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "DisclosureStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EdiMessage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TransactionSet = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ControlNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true, collation: "C"),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    DocumentNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Content = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EdiMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EdiMessage_EdiPartner_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "EdiPartner",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EdiMessage_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EngineeringChangeItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromBomId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToBomId = table.Column<Guid>(type: "uuid", nullable: true),
                    NewPartId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineeringChangeItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeItems_BillOfMaterial_FromBomId",
                        column: x => x.FromBomId,
                        principalTable: "BillOfMaterial",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeItems_BillOfMaterial_ToBomId",
                        column: x => x.ToBomId,
                        principalTable: "BillOfMaterial",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeItems_EngineeringChanges_ChangeId",
                        column: x => x.ChangeId,
                        principalTable: "EngineeringChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeItems_Part_NewPartId",
                        column: x => x.NewPartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeItems_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EngineeringChangeWorkOrderReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FlaggedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineeringChangeWorkOrderReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeWorkOrderReviews_EngineeringChanges_Change~",
                        column: x => x.ChangeId,
                        principalTable: "EngineeringChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EngineeringChangeWorkOrderReviews_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    OutputFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true, collation: "C"),
                    OutputBlobKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, collation: "C"),
                    OutputBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectorRuns_IntegrationConnectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "IntegrationConnectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KitLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderComponentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequirementId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuantityRequired = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityStaged = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityIssued = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KitLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KitLine_Kit_KitId",
                        column: x => x.KitId,
                        principalTable: "Kit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KitLine_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitLine_ProjectMaterialRequirement_RequirementId",
                        column: x => x.RequirementId,
                        principalTable: "ProjectMaterialRequirement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitLine_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitLine_WorkOrderComponent_WorkOrderComponentId",
                        column: x => x.WorkOrderComponentId,
                        principalTable: "WorkOrderComponent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LandedChargeLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LandedChargeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ToInventory = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ToIssued = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ToVariance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LandedChargeLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LandedChargeLine_InventoryTransaction_ReceiptTransactionId",
                        column: x => x.ReceiptTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LandedChargeLine_LandedCharge_LandedChargeId",
                        column: x => x.LandedChargeId,
                        principalTable: "LandedCharge",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LandedChargeLine_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LandedChargeLine_PurchaseReceipt_PurchaseReceiptId",
                        column: x => x.PurchaseReceiptId,
                        principalTable: "PurchaseReceipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationLoad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkCenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLoad", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationLoad_OperationSchedule_OperationScheduleId",
                        column: x => x.OperationScheduleId,
                        principalTable: "OperationSchedule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OperationLoad_WorkCenter_WorkCenterId",
                        column: x => x.WorkCenterId,
                        principalTable: "WorkCenter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CountTag",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PhysicalInventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagNumber = table.Column<int>(type: "integer", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    StockStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Available"),
                    FrozenQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    IsBlankTag = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CountedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    CountedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CountedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecountQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    RecountedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RecountedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    AdjustmentTransactionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CountTag", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CountTag_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CountTag_InventoryTransaction_AdjustmentTransactionId",
                        column: x => x.AdjustmentTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CountTag_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CountTag_PhysicalInventory_PhysicalInventoryId",
                        column: x => x.PhysicalInventoryId,
                        principalTable: "PhysicalInventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CountTag_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhysicalInventoryBin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PhysicalInventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalInventoryBin", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhysicalInventoryBin_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhysicalInventoryBin_PhysicalInventory_PhysicalInventoryId",
                        column: x => x.PhysicalInventoryId,
                        principalTable: "PhysicalInventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcurementFileApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    LevelName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementFileApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcurementFileApprovals_ProcurementFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "ProcurementFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderAcknowledgementLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcknowledgementId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromisedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderAcknowledgementLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderAcknowledgementLine_PurchaseOrderAcknowledgeme~",
                        column: x => x.AcknowledgementId,
                        principalTable: "PurchaseOrderAcknowledgement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderAcknowledgementLine_PurchaseOrderLine_Purchase~",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspectionCharacteristics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Nominal = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    LowerLimit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    UpperLimit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspectionCharacteristics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityInspectionCharacteristics_QualityInspectionPlans_Pla~",
                        column: x => x.PlanId,
                        principalTable: "QualityInspectionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RfqAward",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RfqId = table.Column<Guid>(type: "uuid", nullable: false),
                    AwardedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    AwardedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VendorsInvited = table.Column<int>(type: "integer", nullable: false),
                    QuotesReceived = table.Column<int>(type: "integer", nullable: false),
                    SoleSource = table.Column<bool>(type: "boolean", nullable: false),
                    SoleSourceJustification = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    PriceReasonableness = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    AwardedTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ComparisonJson = table.Column<string>(type: "character varying(400000)", maxLength: 400000, nullable: false, collation: "C"),
                    PurchaseOrderNumbers = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfqAward", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RfqAward_Rfq_RfqId",
                        column: x => x.RfqId,
                        principalTable: "Rfq",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RfqVendor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RfqId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InvitedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeclineReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfqVendor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RfqVendor_Rfq_RfqId",
                        column: x => x.RfqId,
                        principalTable: "Rfq",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RfqVendor_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorQuote",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RfqId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    QuoteDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: false),
                    Terms = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttachmentFileId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorQuote", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorQuote_Rfq_RfqId",
                        column: x => x.RfqId,
                        principalTable: "Rfq",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorQuote_StoredFile_AttachmentFileId",
                        column: x => x.AttachmentFileId,
                        principalTable: "StoredFile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorQuote_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PickList",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickList", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickList_SalesOrder_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemNumber = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    QuantityOrdered = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityShipped = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    NeedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ShipToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ShipToAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ShipToCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    StockNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    UidRequired = table.Column<bool>(type: "boolean", nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrderLine_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLine_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLine_SalesOrder_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Shipment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-00000000c001")),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentNumber = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShipDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Carrier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    BillOfLading = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    TrackingNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ShipToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ShipToAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ShipToCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, collation: "C"),
                    IsFinal = table.Column<bool>(type: "boolean", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shipment_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipment_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipment_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipment_SalesOrder_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShipNoticeLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipNoticeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipNoticeLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipNoticeLine_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipNoticeLine_ShipNotice_ShipNoticeId",
                        column: x => x.ShipNoticeId,
                        principalTable: "ShipNotice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierSignInToken",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierSignInToken", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierSignInToken_SupplierContact_ContactId",
                        column: x => x.ContactId,
                        principalTable: "SupplierContact",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxCodeAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<int>(type: "integer", nullable: false),
                    TargetKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    TaxCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCodeAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxCodeAssignments_TaxCodes_TaxCodeId",
                        column: x => x.TaxCodeId,
                        principalTable: "TaxCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApBillTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    JurisdictionId = table.Column<Guid>(type: "uuid", nullable: true),
                    VendorChargedTax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxCodeOverrideId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaxableAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    UseTaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UseTaxJournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApBillTaxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApBillTaxes_ApBill_BillId",
                        column: x => x.BillId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillTaxes_GlJournalEntries_UseTaxJournalEntryId",
                        column: x => x.UseTaxJournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillTaxes_TaxCodes_TaxCodeOverrideId",
                        column: x => x.TaxCodeOverrideId,
                        principalTable: "TaxCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApBillTaxes_TaxJurisdictions_JurisdictionId",
                        column: x => x.JurisdictionId,
                        principalTable: "TaxJurisdictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerTaxProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipToJurisdictionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerTaxProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerTaxProfiles_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerTaxProfiles_TaxJurisdictions_ShipToJurisdictionId",
                        column: x => x.ShipToJurisdictionId,
                        principalTable: "TaxJurisdictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    JurisdictionId = table.Column<Guid>(type: "uuid", nullable: true),
                    JurisdictionOverrideId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaxableAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExemptAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    StateTax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CountyTax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CityTax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SpecialTax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExemptionReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    CertificateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Engine = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    CalculatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CalculatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceTaxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceTaxes_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceTaxes_TaxExemptionCertificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "TaxExemptionCertificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceTaxes_TaxJurisdictions_JurisdictionId",
                        column: x => x.JurisdictionId,
                        principalTable: "TaxJurisdictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceTaxes_TaxJurisdictions_JurisdictionOverrideId",
                        column: x => x.JurisdictionOverrideId,
                        principalTable: "TaxJurisdictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesTaxSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesTaxPayableAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    UseTaxPayableAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    UseTaxExpenseAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    HomeJurisdictionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultSalesTaxCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultPurchaseTaxCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    GovernmentCategories = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Engine = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTaxSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesTaxSettings_GlAccounts_SalesTaxPayableAccountId",
                        column: x => x.SalesTaxPayableAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxSettings_GlAccounts_UseTaxExpenseAccountId",
                        column: x => x.UseTaxExpenseAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxSettings_GlAccounts_UseTaxPayableAccountId",
                        column: x => x.UseTaxPayableAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxSettings_TaxCodes_DefaultPurchaseTaxCodeId",
                        column: x => x.DefaultPurchaseTaxCodeId,
                        principalTable: "TaxCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxSettings_TaxCodes_DefaultSalesTaxCodeId",
                        column: x => x.DefaultSalesTaxCodeId,
                        principalTable: "TaxCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxSettings_TaxJurisdictions_HomeJurisdictionId",
                        column: x => x.HomeJurisdictionId,
                        principalTable: "TaxJurisdictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JurisdictionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StateRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CountyRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CityRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    SpecialRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxRates_TaxJurisdictions_JurisdictionId",
                        column: x => x.JurisdictionId,
                        principalTable: "TaxJurisdictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    DedupKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatusCode = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    RedeliveryOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationDeliveries_IntegrationConnectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "IntegrationConnectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntegrationDeliveries_IntegrationOutboxEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "IntegrationOutboxEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntegrationDeliveries_WebhookEndpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "WebhookEndpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KitPick",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KitId = table.Column<Guid>(type: "uuid", nullable: false),
                    KitLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromWarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromBinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    TransferInTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueTransactionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KitPick", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KitPick_Bin_FromBinId",
                        column: x => x.FromBinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitPick_InventoryTransaction_IssueTransactionId",
                        column: x => x.IssueTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitPick_InventoryTransaction_TransferInTransactionId",
                        column: x => x.TransferInTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitPick_KitLine_KitLineId",
                        column: x => x.KitLineId,
                        principalTable: "KitLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitPick_Kit_KitId",
                        column: x => x.KitId,
                        principalTable: "Kit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitPick_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KitPick_Warehouse_FromWarehouseId",
                        column: x => x.FromWarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RfqLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RfqId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    NeededBy = table.Column<DateOnly>(type: "date", nullable: true),
                    EstimatedUnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    OdcType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequisitionLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    AwardedQuoteLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    AwardId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfqLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RfqLine_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_PurchaseRequisitionLine_RequisitionLineId",
                        column: x => x.RequisitionLineId,
                        principalTable: "PurchaseRequisitionLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_RfqAward_AwardId",
                        column: x => x.AwardId,
                        principalTable: "RfqAward",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RfqLine_Rfq_RfqId",
                        column: x => x.RfqId,
                        principalTable: "Rfq",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RfqLine_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PickListLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PickListId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityPicked = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    PickedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    PickedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickListLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickListLine_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PickListLine_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PickListLine_PickList_PickListId",
                        column: x => x.PickListId,
                        principalTable: "PickList",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PickListLine_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PickListLine_SalesOrderLine_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalTable: "SalesOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PickListLine_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentPackage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Length = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Width = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Height = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentPackage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentPackage_Shipment_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WawfReceivingReport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ExportedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ExportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WawfReceivingReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WawfReceivingReport_Shipment_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    RequestUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    RequestHeaders = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    RequestBody = table.Column<string>(type: "character varying(70000)", maxLength: 70000, nullable: false, collation: "C"),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseHeaders = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    ResponseBody = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationDeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntegrationDeliveryAttempts_IntegrationDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "IntegrationDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorQuoteLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    RfqLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    NoBid = table.Column<bool>(type: "boolean", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorQuoteLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorQuoteLine_RfqLine_RfqLineId",
                        column: x => x.RfqLineId,
                        principalTable: "RfqLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorQuoteLine_VendorQuote_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "VendorQuote",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PickListLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    PackageNumber = table.Column<int>(type: "integer", nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CostAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ChargedToWbs = table.Column<bool>(type: "boolean", nullable: false),
                    InventoryTransactionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_InventoryTransaction_InventoryTransactionId",
                        column: x => x.InventoryTransactionId,
                        principalTable: "InventoryTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_PickListLine_PickListLineId",
                        column: x => x.PickListLineId,
                        principalTable: "PickListLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_SalesOrderLine_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalTable: "SalesOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_Shipment_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorQuotePriceBreak",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinimumQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorQuotePriceBreak", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorQuotePriceBreak_VendorQuoteLine_QuoteLineId",
                        column: x => x.QuoteLineId,
                        principalTable: "VendorQuoteLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QualityCorrectiveActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    NonconformanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    Problem = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    RootCauseCategory = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RootCause = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Action = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    PreventiveAction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ImplementedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    VerifiedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    VerificationNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityCorrectiveActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityCorrectiveActions_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityCorrectiveActions_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspectionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    BinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityAccepted = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityRejected = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspectionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityInspectionItems_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspectionItems_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspectionItems_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspectionResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CharacteristicId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    MeasuredValue = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspectionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityInspectionResults_QualityInspectionCharacteristics_C~",
                        column: x => x.CharacteristicId,
                        principalTable: "QualityInspectionCharacteristics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityInspections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    QuantityAccepted = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityRejected = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DefectsFound = table.Column<int>(type: "integer", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationSequence = table.Column<int>(type: "integer", nullable: true),
                    ReadyOn = table.Column<DateOnly>(type: "date", nullable: false),
                    InspectedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    InspectorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    NonconformanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityInspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityInspections_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_PurchaseReceipt_PurchaseReceiptId",
                        column: x => x.PurchaseReceiptId,
                        principalTable: "PurchaseReceipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_QualityInspectionPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "QualityInspectionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityInspections_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityNonconformances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefectCodes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    Disposition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DispositionNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    ScrapAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScrapWbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    DispositionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReturnAuthorization = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ValueAtCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReworkWorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebitMemoId = table.Column<Guid>(type: "uuid", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityNonconformances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_GlAccounts_ScrapAccountId",
                        column: x => x.ScrapAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_QualityInspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "QualityInspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_WbsElement_ScrapWbsElementId",
                        column: x => x.ScrapWbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_WorkOrder_ReworkWorkOrderId",
                        column: x => x.ReworkWorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformances_WorkOrder_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityNonconformanceItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NonconformanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Ownership = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityNonconformanceItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityNonconformanceItems_Bin_BinId",
                        column: x => x.BinId,
                        principalTable: "Bin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformanceItems_Project_OwnerProjectId",
                        column: x => x.OwnerProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityNonconformanceItems_QualityNonconformances_Nonconfor~",
                        column: x => x.NonconformanceId,
                        principalTable: "QualityNonconformances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QualityNonconformanceItems_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityVendorDebitMemos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    NonconformanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReturnAuthorization = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityVendorDebitMemos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityVendorDebitMemos_Part_PartId",
                        column: x => x.PartId,
                        principalTable: "Part",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityVendorDebitMemos_PurchaseOrderLine_PurchaseOrderLine~",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityVendorDebitMemos_QualityNonconformances_Nonconforman~",
                        column: x => x.NonconformanceId,
                        principalTable: "QualityNonconformances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityVendorDebitMemos_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBalance_Status",
                table: "InventoryBalance",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransfer_FromCompanyId",
                table: "AssetTransfer",
                column: "FromCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransfer_FromContractId",
                table: "AssetTransfer",
                column: "FromContractId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransfer_JournalEntryId",
                table: "AssetTransfer",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransfer_ToCompanyId",
                table: "AssetTransfer",
                column: "ToCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTransfer_ToContractId",
                table: "AssetTransfer",
                column: "ToContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillTaxes_BillId",
                table: "ApBillTaxes",
                column: "BillId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApBillTaxes_JurisdictionId",
                table: "ApBillTaxes",
                column: "JurisdictionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillTaxes_TaxCodeOverrideId",
                table: "ApBillTaxes",
                column: "TaxCodeOverrideId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillTaxes_UseTaxJournalEntryId",
                table: "ApBillTaxes",
                column: "UseTaxJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ServiceUserEmail",
                table: "ApiKeys",
                column: "ServiceUserEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetTaxBooks_AssetId_Book",
                table: "AssetTaxBooks",
                columns: new[] { "AssetId", "Book" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackflushSetting_BinId",
                table: "BackflushSetting",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_BackflushSetting_BomId",
                table: "BackflushSetting",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_BackflushSetting_PartId_BomId",
                table: "BackflushSetting",
                columns: new[] { "PartId", "BomId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackflushSetting_WarehouseId",
                table: "BackflushSetting",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_BackflushShortage_PartId",
                table: "BackflushShortage",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_BackflushShortage_WorkOrderComponentId",
                table: "BackflushShortage",
                column: "WorkOrderComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_BackflushShortage_WorkOrderId_WorkOrderComponentId",
                table: "BackflushShortage",
                columns: new[] { "WorkOrderId", "WorkOrderComponentId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAchSettings_BankAccountId",
                table: "BankAchSettings",
                column: "BankAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapacityScheduleRun_RunAtUtc",
                table: "CapacityScheduleRun",
                column: "RunAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorRuns_ConnectorId_StartedAtUtc",
                table: "ConnectorRuns",
                columns: new[] { "ConnectorId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CountTag_AdjustmentTransactionId",
                table: "CountTag",
                column: "AdjustmentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_CountTag_BinId",
                table: "CountTag",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_CountTag_OwnerProjectId",
                table: "CountTag",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_CountTag_PartId",
                table: "CountTag",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_CountTag_PhysicalInventoryId_TagNumber",
                table: "CountTag",
                columns: new[] { "PhysicalInventoryId", "TagNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTaxProfiles_CustomerId",
                table: "CustomerTaxProfiles",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTaxProfiles_ShipToJurisdictionId",
                table: "CustomerTaxProfiles",
                column: "ShipToJurisdictionId");

            migrationBuilder.CreateIndex(
                name: "IX_DisclosureAnswers_StatementId_ItemKey",
                table: "DisclosureAnswers",
                columns: new[] { "StatementId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DisclosureStatements_Form_Version",
                table: "DisclosureStatements",
                columns: new[] { "Form", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DisclosureStatements_PreviousVersionId",
                table: "DisclosureStatements",
                column: "PreviousVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EdiMessage_CreatedAtUtc",
                table: "EdiMessage",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EdiMessage_PartnerId_Direction_ControlNumber",
                table: "EdiMessage",
                columns: new[] { "PartnerId", "Direction", "ControlNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_EdiMessage_VendorId",
                table: "EdiMessage",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_EdiPartner_InterchangeQualifier_InterchangeId",
                table: "EdiPartner",
                columns: new[] { "InterchangeQualifier", "InterchangeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EdiPartner_VendorId",
                table: "EdiPartner",
                column: "VendorId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeItems_ChangeId",
                table: "EngineeringChangeItems",
                column: "ChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeItems_FromBomId",
                table: "EngineeringChangeItems",
                column: "FromBomId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeItems_NewPartId",
                table: "EngineeringChangeItems",
                column: "NewPartId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeItems_PartId",
                table: "EngineeringChangeItems",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeItems_ToBomId",
                table: "EngineeringChangeItems",
                column: "ToBomId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChanges_Number",
                table: "EngineeringChanges",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChanges_Status",
                table: "EngineeringChanges",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeWorkOrderReviews_ChangeId_WorkOrderId",
                table: "EngineeringChangeWorkOrderReviews",
                columns: new[] { "ChangeId", "WorkOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineeringChangeWorkOrderReviews_WorkOrderId_Status",
                table: "EngineeringChangeWorkOrderReviews",
                columns: new[] { "WorkOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationConnectors_IsActive_NextRunAtUtc",
                table: "IntegrationConnectors",
                columns: new[] { "IsActive", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveries_ConnectorId_CreatedAtUtc",
                table: "IntegrationDeliveries",
                columns: new[] { "ConnectorId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveries_DedupKey",
                table: "IntegrationDeliveries",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveries_EndpointId_CreatedAtUtc",
                table: "IntegrationDeliveries",
                columns: new[] { "EndpointId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveries_EventId",
                table: "IntegrationDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveries_Status_NextAttemptAtUtc",
                table: "IntegrationDeliveries",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveryAttempts_DeliveryId_AttemptNumber",
                table: "IntegrationDeliveryAttempts",
                columns: new[] { "DeliveryId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOutboxEvents_DispatchedAtUtc_OccurredAtUtc",
                table: "IntegrationOutboxEvents",
                columns: new[] { "DispatchedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOutboxEvents_OccurredAtUtc",
                table: "IntegrationOutboxEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceTaxes_CertificateId",
                table: "InvoiceTaxes",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceTaxes_InvoiceId",
                table: "InvoiceTaxes",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceTaxes_JurisdictionId",
                table: "InvoiceTaxes",
                column: "JurisdictionId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceTaxes_JurisdictionOverrideId",
                table: "InvoiceTaxes",
                column: "JurisdictionOverrideId");

            migrationBuilder.CreateIndex(
                name: "IX_Kit_Number",
                table: "Kit",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Kit_ProjectId",
                table: "Kit",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Kit_StagingBinId",
                table: "Kit",
                column: "StagingBinId");

            migrationBuilder.CreateIndex(
                name: "IX_Kit_StagingWarehouseId",
                table: "Kit",
                column: "StagingWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Kit_Status_StagingBinId",
                table: "Kit",
                columns: new[] { "Status", "StagingBinId" });

            migrationBuilder.CreateIndex(
                name: "IX_Kit_WbsElementId",
                table: "Kit",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_Kit_WorkOrderId",
                table: "Kit",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_KitLine_KitId",
                table: "KitLine",
                column: "KitId");

            migrationBuilder.CreateIndex(
                name: "IX_KitLine_PartId",
                table: "KitLine",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_KitLine_RequirementId",
                table: "KitLine",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_KitLine_WbsElementId",
                table: "KitLine",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_KitLine_WorkOrderComponentId",
                table: "KitLine",
                column: "WorkOrderComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_FromBinId",
                table: "KitPick",
                column: "FromBinId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_FromWarehouseId",
                table: "KitPick",
                column: "FromWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_IssueTransactionId",
                table: "KitPick",
                column: "IssueTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_KitId",
                table: "KitPick",
                column: "KitId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_KitLineId",
                table: "KitPick",
                column: "KitLineId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_OwnerProjectId",
                table: "KitPick",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_KitPick_TransferInTransactionId",
                table: "KitPick",
                column: "TransferInTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedCharge_ApBillLineId",
                table: "LandedCharge",
                column: "ApBillLineId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedCharge_CreditAccountId",
                table: "LandedCharge",
                column: "CreditAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedCharge_JournalEntryId",
                table: "LandedCharge",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedCharge_Number",
                table: "LandedCharge",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LandedCharge_VendorId",
                table: "LandedCharge",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedChargeLine_LandedChargeId",
                table: "LandedChargeLine",
                column: "LandedChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedChargeLine_PartId",
                table: "LandedChargeLine",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedChargeLine_PurchaseReceiptId",
                table: "LandedChargeLine",
                column: "PurchaseReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_LandedChargeLine_ReceiptTransactionId",
                table: "LandedChargeLine",
                column: "ReceiptTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLoad_OperationScheduleId",
                table: "OperationLoad",
                column: "OperationScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLoad_WorkCenterId_Date",
                table: "OperationLoad",
                columns: new[] { "WorkCenterId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationSchedule_WorkCenterId_QueuePosition",
                table: "OperationSchedule",
                columns: new[] { "WorkCenterId", "QueuePosition" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationSchedule_WorkOrderId",
                table: "OperationSchedule",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationSchedule_WorkOrderOperationId",
                table: "OperationSchedule",
                column: "WorkOrderOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentFileDownloads_BankAccountId_DownloadedAtUtc",
                table: "PaymentFileDownloads",
                columns: new[] { "BankAccountId", "DownloadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentFileDownloads_RunId",
                table: "PaymentFileDownloads",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalInventory_JournalEntryId",
                table: "PhysicalInventory",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalInventory_Number",
                table: "PhysicalInventory",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalInventory_Status_WarehouseId",
                table: "PhysicalInventory",
                columns: new[] { "Status", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalInventory_WarehouseId",
                table: "PhysicalInventory",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalInventoryBin_BinId",
                table: "PhysicalInventoryBin",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalInventoryBin_PhysicalInventoryId_BinId",
                table: "PhysicalInventoryBin",
                columns: new[] { "PhysicalInventoryId", "BinId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickList_Number",
                table: "PickList",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickList_SalesOrderId_Status",
                table: "PickList",
                columns: new[] { "SalesOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PickListLine_BinId",
                table: "PickListLine",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListLine_OwnerProjectId",
                table: "PickListLine",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListLine_PartId_BinId",
                table: "PickListLine",
                columns: new[] { "PartId", "BinId" });

            migrationBuilder.CreateIndex(
                name: "IX_PickListLine_PickListId",
                table: "PickListLine",
                column: "PickListId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListLine_SalesOrderLineId",
                table: "PickListLine",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PickListLine_WarehouseId",
                table: "PickListLine",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_PositivePayLayouts_Name",
                table: "PositivePayLayouts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementFileApprovals_FileId",
                table: "ProcurementFileApprovals",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementFiles_PurchaseOrderId",
                table: "ProcurementFiles",
                column: "PurchaseOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementFiles_SubcontractId",
                table: "ProcurementFiles",
                column: "SubcontractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAcknowledgement_PurchaseOrderId",
                table: "PurchaseOrderAcknowledgement",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAcknowledgement_VendorId",
                table: "PurchaseOrderAcknowledgement",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAcknowledgementLine_AcknowledgementId",
                table: "PurchaseOrderAcknowledgementLine",
                column: "AcknowledgementId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAcknowledgementLine_PurchaseOrderLineId",
                table: "PurchaseOrderAcknowledgementLine",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityAttachments_FileId",
                table: "QualityAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityAttachments_OwnerType_OwnerId",
                table: "QualityAttachments",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityCorrectiveActions_NonconformanceId",
                table: "QualityCorrectiveActions",
                column: "NonconformanceId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityCorrectiveActions_Number",
                table: "QualityCorrectiveActions",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityCorrectiveActions_PartId",
                table: "QualityCorrectiveActions",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityCorrectiveActions_Status_DueDate",
                table: "QualityCorrectiveActions",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityCorrectiveActions_VendorId",
                table: "QualityCorrectiveActions",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityDefectCodes_Code",
                table: "QualityDefectCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionCharacteristics_PlanId",
                table: "QualityInspectionCharacteristics",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionItems_BinId",
                table: "QualityInspectionItems",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionItems_InspectionId",
                table: "QualityInspectionItems",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionItems_OwnerProjectId",
                table: "QualityInspectionItems",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionItems_WarehouseId",
                table: "QualityInspectionItems",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionPlans_Code",
                table: "QualityInspectionPlans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionPlans_PartId_Kind",
                table: "QualityInspectionPlans",
                columns: new[] { "PartId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionResults_CharacteristicId",
                table: "QualityInspectionResults",
                column: "CharacteristicId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspectionResults_InspectionId",
                table: "QualityInspectionResults",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_NonconformanceId",
                table: "QualityInspections",
                column: "NonconformanceId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_Number",
                table: "QualityInspections",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_PartId",
                table: "QualityInspections",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_PlanId",
                table: "QualityInspections",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_PurchaseOrderId",
                table: "QualityInspections",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_PurchaseOrderLineId",
                table: "QualityInspections",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_PurchaseReceiptId",
                table: "QualityInspections",
                column: "PurchaseReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_Status_Kind",
                table: "QualityInspections",
                columns: new[] { "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_VendorId",
                table: "QualityInspections",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityInspections_WorkOrderId",
                table: "QualityInspections",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformanceItems_BinId",
                table: "QualityNonconformanceItems",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformanceItems_NonconformanceId",
                table: "QualityNonconformanceItems",
                column: "NonconformanceId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformanceItems_OwnerProjectId",
                table: "QualityNonconformanceItems",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformanceItems_WarehouseId",
                table: "QualityNonconformanceItems",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_InspectionId",
                table: "QualityNonconformances",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_JournalEntryId",
                table: "QualityNonconformances",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_Number",
                table: "QualityNonconformances",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_PartId",
                table: "QualityNonconformances",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_PurchaseOrderLineId",
                table: "QualityNonconformances",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_ReworkWorkOrderId",
                table: "QualityNonconformances",
                column: "ReworkWorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_ScrapAccountId",
                table: "QualityNonconformances",
                column: "ScrapAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_ScrapWbsElementId",
                table: "QualityNonconformances",
                column: "ScrapWbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_Status",
                table: "QualityNonconformances",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_VendorId",
                table: "QualityNonconformances",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityNonconformances_WorkOrderId",
                table: "QualityNonconformances",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityVendorDebitMemos_NonconformanceId",
                table: "QualityVendorDebitMemos",
                column: "NonconformanceId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityVendorDebitMemos_Number",
                table: "QualityVendorDebitMemos",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityVendorDebitMemos_PartId",
                table: "QualityVendorDebitMemos",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityVendorDebitMemos_PurchaseOrderLineId",
                table: "QualityVendorDebitMemos",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityVendorDebitMemos_VendorId_Status",
                table: "QualityVendorDebitMemos",
                columns: new[] { "VendorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceAdvices_RunId",
                table: "RemittanceAdvices",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RemittanceAdvices_VendorId",
                table: "RemittanceAdvices",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_Rfq_Number",
                table: "Rfq",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rfq_Status",
                table: "Rfq",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RfqAward_RfqId",
                table: "RfqAward",
                column: "RfqId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_AccountId",
                table: "RfqLine",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_AwardId",
                table: "RfqLine",
                column: "AwardId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_OrgUnitId",
                table: "RfqLine",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_PartId",
                table: "RfqLine",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_ProjectId",
                table: "RfqLine",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_PurchaseOrderLineId",
                table: "RfqLine",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_RequisitionLineId",
                table: "RfqLine",
                column: "RequisitionLineId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_RfqId",
                table: "RfqLine",
                column: "RfqId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqLine_WbsElementId",
                table: "RfqLine",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqVendor_RfqId_VendorId",
                table: "RfqVendor",
                columns: new[] { "RfqId", "VendorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RfqVendor_VendorId",
                table: "RfqVendor",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_CompanyId",
                table: "SalesOrder",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_ContractId",
                table: "SalesOrder",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_CustomerId",
                table: "SalesOrder",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_Number",
                table: "SalesOrder",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_ProjectId",
                table: "SalesOrder",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_Status_OrderDate",
                table: "SalesOrder",
                columns: new[] { "Status", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrder_WbsElementId",
                table: "SalesOrder",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLine_ClinId",
                table: "SalesOrderLine",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLine_PartId",
                table: "SalesOrderLine",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLine_SalesOrderId_LineNumber",
                table: "SalesOrderLine",
                columns: new[] { "SalesOrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxSettings_DefaultPurchaseTaxCodeId",
                table: "SalesTaxSettings",
                column: "DefaultPurchaseTaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxSettings_DefaultSalesTaxCodeId",
                table: "SalesTaxSettings",
                column: "DefaultSalesTaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxSettings_HomeJurisdictionId",
                table: "SalesTaxSettings",
                column: "HomeJurisdictionId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxSettings_SalesTaxPayableAccountId",
                table: "SalesTaxSettings",
                column: "SalesTaxPayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxSettings_UseTaxExpenseAccountId",
                table: "SalesTaxSettings",
                column: "UseTaxExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxSettings_UseTaxPayableAccountId",
                table: "SalesTaxSettings",
                column: "UseTaxPayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_CompanyId",
                table: "Shipment",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_InvoiceId",
                table: "Shipment",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_JournalEntryId",
                table: "Shipment",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_Number",
                table: "Shipment",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_SalesOrderId_Status",
                table: "Shipment",
                columns: new[] { "SalesOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_ShipDate",
                table: "Shipment",
                column: "ShipDate");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_BinId",
                table: "ShipmentLine",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_InventoryTransactionId",
                table: "ShipmentLine",
                column: "InventoryTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_OwnerProjectId",
                table: "ShipmentLine",
                column: "OwnerProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_PartId",
                table: "ShipmentLine",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_PickListLineId",
                table: "ShipmentLine",
                column: "PickListLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_SalesOrderLineId",
                table: "ShipmentLine",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_ShipmentId",
                table: "ShipmentLine",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_WarehouseId",
                table: "ShipmentLine",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentPackage_ShipmentId_Number",
                table: "ShipmentPackage",
                columns: new[] { "ShipmentId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipNotice_Number",
                table: "ShipNotice",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipNotice_PurchaseOrderId",
                table: "ShipNotice",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipNotice_ReceiptId",
                table: "ShipNotice",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipNotice_VendorId_Status",
                table: "ShipNotice",
                columns: new[] { "VendorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipNoticeLine_PurchaseOrderLineId",
                table: "ShipNoticeLine",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipNoticeLine_ShipNoticeId",
                table: "ShipNoticeLine",
                column: "ShipNoticeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierContact_Email",
                table: "SupplierContact",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierContact_VendorId",
                table: "SupplierContact",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoice_BillId",
                table: "SupplierInvoice",
                column: "BillId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoice_PurchaseOrderId",
                table: "SupplierInvoice",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoice_VendorId",
                table: "SupplierInvoice",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPortalEvent_OccurredAtUtc",
                table: "SupplierPortalEvent",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPortalEvent_VendorId_OccurredAtUtc",
                table: "SupplierPortalEvent",
                columns: new[] { "VendorId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSignInToken_ContactId",
                table: "SupplierSignInToken",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSignInToken_TokenHash",
                table: "SupplierSignInToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeAssignments_Target_TargetKey",
                table: "TaxCodeAssignments",
                columns: new[] { "Target", "TargetKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeAssignments_TaxCodeId",
                table: "TaxCodeAssignments",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_Code",
                table: "TaxCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxExemptionCertificates_CustomerId",
                table: "TaxExemptionCertificates",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxJurisdictions_Code",
                table: "TaxJurisdictions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_JurisdictionId_EffectiveDate",
                table: "TaxRates",
                columns: new[] { "JurisdictionId", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorBankAccounts_VendorId",
                table: "VendorBankAccounts",
                column: "VendorId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorBankChanges_VendorId_Status",
                table: "VendorBankChanges",
                columns: new[] { "VendorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorQuote_AttachmentFileId",
                table: "VendorQuote",
                column: "AttachmentFileId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorQuote_RfqId_VendorId",
                table: "VendorQuote",
                columns: new[] { "RfqId", "VendorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorQuote_VendorId",
                table: "VendorQuote",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorQuoteLine_QuoteId_RfqLineId",
                table: "VendorQuoteLine",
                columns: new[] { "QuoteId", "RfqLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorQuoteLine_RfqLineId",
                table: "VendorQuoteLine",
                column: "RfqLineId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorQuotePriceBreak_QuoteLineId",
                table: "VendorQuotePriceBreak",
                column: "QuoteLineId");

            migrationBuilder.CreateIndex(
                name: "IX_WawfReceivingReport_ShipmentId",
                table: "WawfReceivingReport",
                column: "ShipmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCenterCalendar_WorkCenterId",
                table: "WorkCenterCalendar",
                column: "WorkCenterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCenterCapacityException_WorkCenterId_Date",
                table: "WorkCenterCapacityException",
                columns: new[] { "WorkCenterId", "Date" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetTransfer_Companies_FromCompanyId",
                table: "AssetTransfer",
                column: "FromCompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetTransfer_Companies_ToCompanyId",
                table: "AssetTransfer",
                column: "ToCompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetTransfer_Contract_FromContractId",
                table: "AssetTransfer",
                column: "FromContractId",
                principalTable: "Contract",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetTransfer_Contract_ToContractId",
                table: "AssetTransfer",
                column: "ToContractId",
                principalTable: "Contract",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetTransfer_GlJournalEntries_JournalEntryId",
                table: "AssetTransfer",
                column: "JournalEntryId",
                principalTable: "GlJournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QualityCorrectiveActions_QualityNonconformances_Nonconforma~",
                table: "QualityCorrectiveActions",
                column: "NonconformanceId",
                principalTable: "QualityNonconformances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QualityInspectionItems_QualityInspections_InspectionId",
                table: "QualityInspectionItems",
                column: "InspectionId",
                principalTable: "QualityInspections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_QualityInspectionResults_QualityInspections_InspectionId",
                table: "QualityInspectionResults",
                column: "InspectionId",
                principalTable: "QualityInspections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_QualityInspections_QualityNonconformances_NonconformanceId",
                table: "QualityInspections",
                column: "NonconformanceId",
                principalTable: "QualityNonconformances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssetTransfer_Companies_FromCompanyId",
                table: "AssetTransfer");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetTransfer_Companies_ToCompanyId",
                table: "AssetTransfer");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetTransfer_Contract_FromContractId",
                table: "AssetTransfer");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetTransfer_Contract_ToContractId",
                table: "AssetTransfer");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetTransfer_GlJournalEntries_JournalEntryId",
                table: "AssetTransfer");

            migrationBuilder.DropForeignKey(
                name: "FK_QualityInspections_QualityNonconformances_NonconformanceId",
                table: "QualityInspections");

            migrationBuilder.DropTable(
                name: "ApBillTaxes");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "AssetTaxBooks");

            migrationBuilder.DropTable(
                name: "BackflushSetting");

            migrationBuilder.DropTable(
                name: "BackflushShortage");

            migrationBuilder.DropTable(
                name: "BankAchSettings");

            migrationBuilder.DropTable(
                name: "CapacityScheduleRun");

            migrationBuilder.DropTable(
                name: "ConnectorRuns");

            migrationBuilder.DropTable(
                name: "CountTag");

            migrationBuilder.DropTable(
                name: "CustomerTaxProfiles");

            migrationBuilder.DropTable(
                name: "DisclosureAnswers");

            migrationBuilder.DropTable(
                name: "EdiMessage");

            migrationBuilder.DropTable(
                name: "EngineeringChangeItems");

            migrationBuilder.DropTable(
                name: "EngineeringChangeWorkOrderReviews");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "InvoiceTaxes");

            migrationBuilder.DropTable(
                name: "KitPick");

            migrationBuilder.DropTable(
                name: "LandedChargeLine");

            migrationBuilder.DropTable(
                name: "OperationLoad");

            migrationBuilder.DropTable(
                name: "PaymentFileDownloads");

            migrationBuilder.DropTable(
                name: "PhysicalInventoryBin");

            migrationBuilder.DropTable(
                name: "PositivePayLayouts");

            migrationBuilder.DropTable(
                name: "ProcurementFileApprovals");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAcknowledgementLine");

            migrationBuilder.DropTable(
                name: "QualityAttachments");

            migrationBuilder.DropTable(
                name: "QualityCorrectiveActions");

            migrationBuilder.DropTable(
                name: "QualityDefectCodes");

            migrationBuilder.DropTable(
                name: "QualityInspectionItems");

            migrationBuilder.DropTable(
                name: "QualityInspectionResults");

            migrationBuilder.DropTable(
                name: "QualityNonconformanceItems");

            migrationBuilder.DropTable(
                name: "QualityVendorDebitMemos");

            migrationBuilder.DropTable(
                name: "RemittanceAdvices");

            migrationBuilder.DropTable(
                name: "RfqVendor");

            migrationBuilder.DropTable(
                name: "SalesTaxSettings");

            migrationBuilder.DropTable(
                name: "ShipmentLine");

            migrationBuilder.DropTable(
                name: "ShipmentPackage");

            migrationBuilder.DropTable(
                name: "ShipNoticeLine");

            migrationBuilder.DropTable(
                name: "SupplierInvoice");

            migrationBuilder.DropTable(
                name: "SupplierPortalEvent");

            migrationBuilder.DropTable(
                name: "SupplierSignInToken");

            migrationBuilder.DropTable(
                name: "TaxCodeAssignments");

            migrationBuilder.DropTable(
                name: "TaxRates");

            migrationBuilder.DropTable(
                name: "VendorBankAccounts");

            migrationBuilder.DropTable(
                name: "VendorBankChanges");

            migrationBuilder.DropTable(
                name: "VendorQuotePriceBreak");

            migrationBuilder.DropTable(
                name: "WawfReceivingReport");

            migrationBuilder.DropTable(
                name: "WorkCenterCalendar");

            migrationBuilder.DropTable(
                name: "WorkCenterCapacityException");

            migrationBuilder.DropTable(
                name: "DisclosureStatements");

            migrationBuilder.DropTable(
                name: "EdiPartner");

            migrationBuilder.DropTable(
                name: "EngineeringChanges");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveries");

            migrationBuilder.DropTable(
                name: "TaxExemptionCertificates");

            migrationBuilder.DropTable(
                name: "KitLine");

            migrationBuilder.DropTable(
                name: "LandedCharge");

            migrationBuilder.DropTable(
                name: "OperationSchedule");

            migrationBuilder.DropTable(
                name: "PhysicalInventory");

            migrationBuilder.DropTable(
                name: "ProcurementFiles");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAcknowledgement");

            migrationBuilder.DropTable(
                name: "QualityInspectionCharacteristics");

            migrationBuilder.DropTable(
                name: "PickListLine");

            migrationBuilder.DropTable(
                name: "ShipNotice");

            migrationBuilder.DropTable(
                name: "SupplierContact");

            migrationBuilder.DropTable(
                name: "TaxCodes");

            migrationBuilder.DropTable(
                name: "TaxJurisdictions");

            migrationBuilder.DropTable(
                name: "VendorQuoteLine");

            migrationBuilder.DropTable(
                name: "Shipment");

            migrationBuilder.DropTable(
                name: "IntegrationConnectors");

            migrationBuilder.DropTable(
                name: "IntegrationOutboxEvents");

            migrationBuilder.DropTable(
                name: "WebhookEndpoints");

            migrationBuilder.DropTable(
                name: "Kit");

            migrationBuilder.DropTable(
                name: "PickList");

            migrationBuilder.DropTable(
                name: "SalesOrderLine");

            migrationBuilder.DropTable(
                name: "RfqLine");

            migrationBuilder.DropTable(
                name: "VendorQuote");

            migrationBuilder.DropTable(
                name: "SalesOrder");

            migrationBuilder.DropTable(
                name: "RfqAward");

            migrationBuilder.DropTable(
                name: "Rfq");

            migrationBuilder.DropTable(
                name: "QualityNonconformances");

            migrationBuilder.DropTable(
                name: "QualityInspections");

            migrationBuilder.DropTable(
                name: "QualityInspectionPlans");

            migrationBuilder.DropIndex(
                name: "IX_InventoryBalance_Status",
                table: "InventoryBalance");

            migrationBuilder.DropIndex(
                name: "IX_AssetTransfer_FromCompanyId",
                table: "AssetTransfer");

            migrationBuilder.DropIndex(
                name: "IX_AssetTransfer_FromContractId",
                table: "AssetTransfer");

            migrationBuilder.DropIndex(
                name: "IX_AssetTransfer_JournalEntryId",
                table: "AssetTransfer");

            migrationBuilder.DropIndex(
                name: "IX_AssetTransfer_ToCompanyId",
                table: "AssetTransfer");

            migrationBuilder.DropIndex(
                name: "IX_AssetTransfer_ToContractId",
                table: "AssetTransfer");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "InventoryTransaction");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "InventoryBalance");

            migrationBuilder.DropColumn(
                name: "DisposalKind",
                table: "FixedAsset");

            migrationBuilder.DropColumn(
                name: "FromCompanyId",
                table: "AssetTransfer");

            migrationBuilder.DropColumn(
                name: "FromContractId",
                table: "AssetTransfer");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "AssetTransfer");

            migrationBuilder.DropColumn(
                name: "ToCompanyId",
                table: "AssetTransfer");

            migrationBuilder.DropColumn(
                name: "ToContractId",
                table: "AssetTransfer");
        }
    }
}
