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
                name: "IX_ConnectorRuns_ConnectorId_StartedAtUtc",
                table: "ConnectorRuns",
                columns: new[] { "ConnectorId", "StartedAtUtc" });

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

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ConnectorRuns");

            migrationBuilder.DropTable(
                name: "EdiMessage");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAcknowledgementLine");

            migrationBuilder.DropTable(
                name: "RfqVendor");

            migrationBuilder.DropTable(
                name: "ShipNoticeLine");

            migrationBuilder.DropTable(
                name: "SupplierInvoice");

            migrationBuilder.DropTable(
                name: "SupplierPortalEvent");

            migrationBuilder.DropTable(
                name: "SupplierSignInToken");

            migrationBuilder.DropTable(
                name: "VendorQuotePriceBreak");

            migrationBuilder.DropTable(
                name: "EdiPartner");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveries");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAcknowledgement");

            migrationBuilder.DropTable(
                name: "ShipNotice");

            migrationBuilder.DropTable(
                name: "SupplierContact");

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

        }
    }
}
