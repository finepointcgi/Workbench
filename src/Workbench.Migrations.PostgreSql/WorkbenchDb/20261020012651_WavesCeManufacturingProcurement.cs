using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <remarks>
    /// The tables of the government contracting module (CPSR files, CAS disclosure, CMMC, incurred cost) were taken out of this migration:
    /// the module creates them (docs/operations/module-extraction-govcon.md). Databases that already ran it are unaffected.
    /// </remarks>
    public partial class WavesCeManufacturingProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

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
                name: "IX_BankAchSettings_BankAccountId",
                table: "BankAchSettings",
                column: "BankAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorRuns_ConnectorId_StartedAtUtc",
                table: "ConnectorRuns",
                columns: new[] { "ConnectorId", "StartedAtUtc" });

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
                name: "IX_PaymentFileDownloads_BankAccountId_DownloadedAtUtc",
                table: "PaymentFileDownloads",
                columns: new[] { "BankAccountId", "DownloadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentFileDownloads_RunId",
                table: "PaymentFileDownloads",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PositivePayLayouts_Name",
                table: "PositivePayLayouts",
                column: "Name",
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

            migrationBuilder.DropTable(
                name: "ApBillTaxes");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "AssetTaxBooks");

            migrationBuilder.DropTable(
                name: "BankAchSettings");

            migrationBuilder.DropTable(
                name: "ConnectorRuns");

            migrationBuilder.DropTable(
                name: "CustomerTaxProfiles");

            migrationBuilder.DropTable(
                name: "EdiMessage");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "InvoiceTaxes");

            migrationBuilder.DropTable(
                name: "PaymentFileDownloads");

            migrationBuilder.DropTable(
                name: "PositivePayLayouts");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAcknowledgementLine");

            migrationBuilder.DropTable(
                name: "RemittanceAdvices");

            migrationBuilder.DropTable(
                name: "RfqVendor");

            migrationBuilder.DropTable(
                name: "SalesTaxSettings");

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
                name: "EdiPartner");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveries");

            migrationBuilder.DropTable(
                name: "TaxExemptionCertificates");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAcknowledgement");

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
                name: "IntegrationConnectors");

            migrationBuilder.DropTable(
                name: "IntegrationOutboxEvents");

            migrationBuilder.DropTable(
                name: "WebhookEndpoints");

            migrationBuilder.DropTable(
                name: "RfqLine");

            migrationBuilder.DropTable(
                name: "VendorQuote");

            migrationBuilder.DropTable(
                name: "RfqAward");

            migrationBuilder.DropTable(
                name: "Rfq");

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
