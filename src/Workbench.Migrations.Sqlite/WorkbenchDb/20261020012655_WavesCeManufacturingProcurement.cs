using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Roles = table.Column<int>(type: "INTEGER", nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ServiceUserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedFrom = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EdiPartner",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InterchangeQualifier = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    InterchangeId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    ApplicationCode = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    Transport = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SendPurchaseOrders = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextControlNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    SecretsProtected = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    EventTypes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RunAtHourUtc = table.Column<int>(type: "INTEGER", nullable: false),
                    RunOnDay = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationConnectors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationOutboxEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveryCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationOutboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderAcknowledgement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Terms = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ShipTo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IssuedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rfq", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShipNotice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShipmentReference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ShipDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExpectedDelivery = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Carrier = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TrackingNumber = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FailedSignIns = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSignInUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SessionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    InvitedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeactivatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeactivatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StatedTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContactId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPortalEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    EventTypes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecretProtected = table.Column<string>(type: "TEXT", nullable: false),
                    SecretHint = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SecretRotatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Health = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    UnhealthySinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextProbeAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFailureAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EdiMessage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    TransactionSet = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PartnerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ControlNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DocumentNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", maxLength: 200000, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    OutputFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    OutputBlobKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    OutputBytes = table.Column<long>(type: "INTEGER", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcknowledgementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PromisedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderAcknowledgementLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderAcknowledgementLine_PurchaseOrderAcknowledgement_AcknowledgementId",
                        column: x => x.AcknowledgementId,
                        principalTable: "PurchaseOrderAcknowledgement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderAcknowledgementLine_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RfqAward",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RfqId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AwardedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    AwardedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VendorsInvited = table.Column<int>(type: "INTEGER", nullable: false),
                    QuotesReceived = table.Column<int>(type: "INTEGER", nullable: false),
                    SoleSource = table.Column<bool>(type: "INTEGER", nullable: false),
                    SoleSourceJustification = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PriceReasonableness = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Rationale = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AwardedTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ComparisonJson = table.Column<string>(type: "TEXT", maxLength: 400000, nullable: false),
                    PurchaseOrderNumbers = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RfqId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    InvitedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeclineReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RfqId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    QuoteDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LeadTimeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Terms = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttachmentFileId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShipNoticeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EndpointId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RedeliveryOfId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RfqId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PartId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    NeededBy = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EstimatedUnitCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OdcType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OrgUnitId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequisitionLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AwardedQuoteLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AwardId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PurchaseOrderLineId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    RequestHeaders = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    RequestBody = table.Column<string>(type: "TEXT", maxLength: 70000, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    ResponseHeaders = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ResponseBody = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RfqLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NoBid = table.Column<bool>(type: "INTEGER", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    LeadTimeDays = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuoteLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MinimumQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false)
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
