using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class WaveAFinanceCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "Vendor",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Project",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "PaymentApplication",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Payment",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "Payment",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Payment",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RealizedGainLoss",
                table: "Payment",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "Payment",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "OdcEntry",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "OdcEntry",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "OdcEntry",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "InvoiceLine",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Invoice",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "Invoice",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Invoice",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionCredited",
                table: "Invoice",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionPaid",
                table: "Invoice",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionTotal",
                table: "Invoice",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "IndirectPoolSetups",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "GlJournalLines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "GlJournalLines",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IntercompanyCompanyId",
                table: "GlJournalLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "GlJournalLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransactionCurrency",
                table: "GlJournalLines",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "GlJournalEntries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "GlAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "FixedAsset",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Employee",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "CustomerBillingProfile",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Contract",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "BankAccounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ApPaymentRun",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<decimal>(
                name: "CashAmount",
                table: "ApPayment",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "ApPayment",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "ApPayment",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RealizedGainLoss",
                table: "ApPayment",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "ApPayment",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "ApBillLine",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ApBill",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000c001"));

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "ApBill",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "ApBill",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionAmount",
                table: "ApBill",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionPaid",
                table: "ApBill",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TransactionRetainage",
                table: "ApBill",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "CardProgram",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentResponsibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LiabilityAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnsubmittedAlertDays = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardProgram", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardProgram_GlAccounts_LiabilityAccountId",
                        column: x => x.LiabilityAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardProgram_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    LegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Ein = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true, collation: "C"),
                    FiscalYearStartMonth = table.Column<int>(type: "integer", nullable: false),
                    HomeCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractGovernmentBilling",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Financing = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IsSmallBusiness = table.Column<bool>(type: "boolean", nullable: false),
                    ProgressPaymentRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    LiquidationRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CostBasis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContractPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    IssueByDodaac = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true, collation: "C"),
                    AdminByDodaac = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true, collation: "C"),
                    PayOfficeDodaac = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true, collation: "C"),
                    ShipToDodaac = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true, collation: "C"),
                    DeliveryDocument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractGovernmentBilling", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractGovernmentBilling_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Currencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Decimals = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CurrencyRevaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    NetGainLoss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReversalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PostedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyRevaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurrencyRevaluations_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CurrencyRevaluations_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CurrencyRevaluations_GlJournalEntries_ReversalEntryId",
                        column: x => x.ReversalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CurrencySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionalCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    RealizedGainLossAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnrealizedGainLossAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencySettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurrencySettings_GlAccounts_RealizedGainLossAccountId",
                        column: x => x.RealizedGainLossAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CurrencySettings_GlAccounts_UnrealizedGainLossAccountId",
                        column: x => x.UnrealizedGainLossAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    ToCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    RateType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlBudgetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdoptedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    AdoptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlBudgetVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlBudgetVersions_FiscalYear_FiscalYearId",
                        column: x => x.FiscalYearId,
                        principalTable: "FiscalYear",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetVersions_GlBudgetVersions_SourceVersionId",
                        column: x => x.SourceVersionId,
                        principalTable: "GlBudgetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlRecurringSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutoGenerate = table.Column<bool>(type: "boolean", nullable: false),
                    LastAutoRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAutoRunSummary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlRecurringSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlRecurringTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Frequency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PostingDay = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Occurrences = table.Column<int>(type: "integer", nullable: true),
                    AmountMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PostAutomatically = table.Column<bool>(type: "boolean", nullable: false),
                    AutoReverse = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlRecurringTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LaborCostPolicy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborCostPolicy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PerformanceBasedPaymentEvent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Criteria = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformanceBasedPaymentEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PerformanceBasedPaymentEvent_Clin_ClinId",
                        column: x => x.ClinId,
                        principalTable: "Clin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PerformanceBasedPaymentEvent_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PerformanceBasedPaymentEvent_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PieeSubmission",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    ExportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExportedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    StatusChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PieeSubmission", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PieeSubmission_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProgressPaymentRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    RequestDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CostsThrough = table.Column<DateOnly>(type: "date", nullable: false),
                    IsSmallBusiness = table.Column<bool>(type: "boolean", nullable: false),
                    ContractPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ProgressPaymentRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    LiquidationRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CostBasis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PaidCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnpaidCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IncurredToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimateToComplete = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubcontractorPaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubcontractorLiquidated = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubcontractorApproved = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousRequested = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CostsOfDelivered = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PriceOfDelivered = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnliquidatedAdvances = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LiquidatedToDate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressPaymentRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProgressPaymentRequest_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProgressPaymentRequest_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TravelAuthorization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, collation: "C"),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: false),
                    TripStart = table.Column<DateOnly>(type: "date", nullable: false),
                    TripEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    EstimatedAirfare = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedLodging = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedMie = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedRentalCar = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedOther = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OtherDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    AdvanceRequested = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AdvanceIssuedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AdvanceIssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AdvanceIssuedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    AdvanceReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    ExpenseReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    RejectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    CancelledBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelAuthorization", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelAuthorization_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TravelAuthorization_ExpenseReport_ExpenseReportId",
                        column: x => x.ExpenseReportId,
                        principalTable: "ExpenseReport",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TravelAuthorization_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TravelAuthorization_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TravelLineCategory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OdcEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelLineCategory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelLineCategory_OdcEntry_OdcEntryId",
                        column: x => x.OdcEntryId,
                        principalTable: "OdcEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CardStatementImport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    ImportedCount = table.Column<int>(type: "integer", nullable: false),
                    DuplicateCount = table.Column<int>(type: "integer", nullable: false),
                    UnmatchedCount = table.Column<int>(type: "integer", nullable: false),
                    ImportedTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ImportedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardStatementImport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardStatementImport_CardProgram_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "CardProgram",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CorporateCard",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    NameOnCard = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorporateCard", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CorporateCard_CardProgram_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "CardProgram",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorporateCard_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyPeriodCloses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyPeriodCloses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyPeriodCloses_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyPeriodCloses_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntercompanyAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterpartyCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueFromAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueToAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntercompanyAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntercompanyAccounts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyAccounts_Companies_CounterpartyCompanyId",
                        column: x => x.CounterpartyCompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyAccounts_GlAccounts_DueFromAccountId",
                        column: x => x.DueFromAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntercompanyAccounts_GlAccounts_DueToAccountId",
                        column: x => x.DueToAccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyAccess",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCompanyAccess", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCompanyAccess_AppUser_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserCompanyAccess_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserCompanySelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCompanySelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCompanySelections_AppUser_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserCompanySelections_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CurrencyRevaluationLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevaluationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, collation: "C"),
                    TransactionBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CarryingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    RevaluedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    GainLoss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyRevaluationLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurrencyRevaluationLines_CurrencyRevaluations_RevaluationId",
                        column: x => x.RevaluationId,
                        principalTable: "CurrencyRevaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CurrencyRevaluationLines_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlBudgetLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlBudgetLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlBudgetLines_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetLines_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetLines_GlBudgetVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "GlBudgetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlBudgetLines_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlPlanningCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdoptedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    AdoptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlPlanningCycles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlPlanningCycles_FiscalYear_FiscalYearId",
                        column: x => x.FiscalYearId,
                        principalTable: "FiscalYear",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlPlanningCycles_GlBudgetVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "GlBudgetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlRecurringRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Occurrence = table.Column<int>(type: "integer", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    GeneratedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlRecurringRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlRecurringRuns_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlRecurringRuns_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GlRecurringRuns_GlJournalEntries_ReversalEntryId",
                        column: x => x.ReversalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlRecurringRuns_GlRecurringTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "GlRecurringTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlRecurringTemplateLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WbsElementId = table.Column<Guid>(type: "uuid", nullable: true),
                    Side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Percent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlRecurringTemplateLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlRecurringTemplateLines_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlRecurringTemplateLines_GlRecurringTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "GlRecurringTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GlRecurringTemplateLines_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlRecurringTemplateLines_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlRecurringTemplateLines_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TravelAuthorizationDestination",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelAuthorizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Destination = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ArriveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    DepartOn = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelAuthorizationDestination", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelAuthorizationDestination_TravelAuthorization_TravelAu~",
                        column: x => x.TravelAuthorizationId,
                        principalTable: "TravelAuthorization",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CardTransaction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CardLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false, collation: "C"),
                    DedupeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Merchant = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    MerchantCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    OdcEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttachedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    IsExcluded = table.Column<bool>(type: "boolean", nullable: false),
                    ExcludedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ExcludedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ExcludedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardTransaction_CardProgram_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "CardProgram",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardTransaction_CardStatementImport_ImportId",
                        column: x => x.ImportId,
                        principalTable: "CardStatementImport",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardTransaction_CorporateCard_CardId",
                        column: x => x.CardId,
                        principalTable: "CorporateCard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardTransaction_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardTransaction_OdcEntry_OdcEntryId",
                        column: x => x.OdcEntryId,
                        principalTable: "OdcEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GlBudgetSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlBudgetSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlBudgetSubmissions_Employee_OwnerEmployeeId",
                        column: x => x.OwnerEmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetSubmissions_GlPlanningCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "GlPlanningCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetSubmissions_OrgUnit_OrgUnitId",
                        column: x => x.OrgUnitId,
                        principalTable: "OrgUnit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GlBudgetSubmissionLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlBudgetSubmissionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GlBudgetSubmissionLines_FiscalPeriod_FiscalPeriodId",
                        column: x => x.FiscalPeriodId,
                        principalTable: "FiscalPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetSubmissionLines_GlAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GlAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GlBudgetSubmissionLines_GlBudgetSubmissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "GlBudgetSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Companies",
                columns: new[] { "Id", "Code", "CreatedAtUtc", "Ein", "FiscalYearStartMonth", "HomeCurrencyCode", "IsActive", "LegalName", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[] { new Guid("00000000-0000-0000-0000-00000000c001"), "MAIN", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 10, "USD", true, "Main company", null, null });

            migrationBuilder.CreateIndex(
                name: "IX_Project_CompanyId",
                table: "Project",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_CompanyId",
                table: "Payment",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_CompanyId",
                table: "Invoice",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_CurrencyCode",
                table: "Invoice",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_IndirectPoolSetups_CompanyId",
                table: "IndirectPoolSetups",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_CompanyId_AccountId",
                table: "GlJournalLines",
                columns: new[] { "CompanyId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalLines_IntercompanyCompanyId",
                table: "GlJournalLines",
                column: "IntercompanyCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GlJournalEntries_CompanyId",
                table: "GlJournalEntries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GlAccounts_CompanyId",
                table: "GlAccounts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAsset_CompanyId",
                table: "FixedAsset",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Employee_CompanyId",
                table: "Employee",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Contract_CompanyId",
                table: "Contract",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_CompanyId",
                table: "BankAccounts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ApPaymentRun_CompanyId",
                table: "ApPaymentRun",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_CompanyId",
                table: "ApBill",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_CurrencyCode",
                table: "ApBill",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_CardProgram_LiabilityAccountId",
                table: "CardProgram",
                column: "LiabilityAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CardProgram_Name",
                table: "CardProgram",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CardProgram_VendorId",
                table: "CardProgram",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_CardStatementImport_ProgramId_ImportedAtUtc",
                table: "CardStatementImport",
                columns: new[] { "ProgramId", "ImportedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CardTransaction_CardId",
                table: "CardTransaction",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_CardTransaction_EmployeeId_TransactionDate",
                table: "CardTransaction",
                columns: new[] { "EmployeeId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CardTransaction_ImportId",
                table: "CardTransaction",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_CardTransaction_OdcEntryId",
                table: "CardTransaction",
                column: "OdcEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CardTransaction_ProgramId_DedupeKey",
                table: "CardTransaction",
                columns: new[] { "ProgramId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Code",
                table: "Companies",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyPeriodCloses_CompanyId_FiscalPeriodId",
                table: "CompanyPeriodCloses",
                columns: new[] { "CompanyId", "FiscalPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyPeriodCloses_FiscalPeriodId",
                table: "CompanyPeriodCloses",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractGovernmentBilling_ContractId",
                table: "ContractGovernmentBilling",
                column: "ContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorporateCard_EmployeeId",
                table: "CorporateCard",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CorporateCard_ProgramId_Last4",
                table: "CorporateCard",
                columns: new[] { "ProgramId", "Last4" });

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_Code",
                table: "Currencies",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRevaluationLines_AccountId",
                table: "CurrencyRevaluationLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRevaluationLines_RevaluationId",
                table: "CurrencyRevaluationLines",
                column: "RevaluationId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRevaluations_FiscalPeriodId",
                table: "CurrencyRevaluations",
                column: "FiscalPeriodId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRevaluations_JournalEntryId",
                table: "CurrencyRevaluations",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRevaluations_ReversalEntryId",
                table: "CurrencyRevaluations",
                column: "ReversalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencySettings_RealizedGainLossAccountId",
                table: "CurrencySettings",
                column: "RealizedGainLossAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencySettings_UnrealizedGainLossAccountId",
                table: "CurrencySettings",
                column: "UnrealizedGainLossAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_FromCurrency_ToCurrency_RateType_EffectiveDate",
                table: "ExchangeRates",
                columns: new[] { "FromCurrency", "ToCurrency", "RateType", "EffectiveDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetLines_AccountId",
                table: "GlBudgetLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetLines_FiscalPeriodId",
                table: "GlBudgetLines",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetLines_OrgUnitId",
                table: "GlBudgetLines",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetLines_VersionId_AccountId_OrgUnitId_FiscalPeriodId",
                table: "GlBudgetLines",
                columns: new[] { "VersionId", "AccountId", "OrgUnitId", "FiscalPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetLines_VersionId_FiscalPeriodId",
                table: "GlBudgetLines",
                columns: new[] { "VersionId", "FiscalPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetSubmissionLines_AccountId",
                table: "GlBudgetSubmissionLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetSubmissionLines_FiscalPeriodId",
                table: "GlBudgetSubmissionLines",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetSubmissionLines_SubmissionId_AccountId_FiscalPeriod~",
                table: "GlBudgetSubmissionLines",
                columns: new[] { "SubmissionId", "AccountId", "FiscalPeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetSubmissions_CycleId_OrgUnitId",
                table: "GlBudgetSubmissions",
                columns: new[] { "CycleId", "OrgUnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetSubmissions_OrgUnitId",
                table: "GlBudgetSubmissions",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetSubmissions_OwnerEmployeeId",
                table: "GlBudgetSubmissions",
                column: "OwnerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetVersions_FiscalYearId_Name",
                table: "GlBudgetVersions",
                columns: new[] { "FiscalYearId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlBudgetVersions_SourceVersionId",
                table: "GlBudgetVersions",
                column: "SourceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_GlPlanningCycles_FiscalYearId",
                table: "GlPlanningCycles",
                column: "FiscalYearId");

            migrationBuilder.CreateIndex(
                name: "IX_GlPlanningCycles_VersionId",
                table: "GlPlanningCycles",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringRuns_FiscalPeriodId",
                table: "GlRecurringRuns",
                column: "FiscalPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringRuns_JournalEntryId",
                table: "GlRecurringRuns",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringRuns_ReversalEntryId",
                table: "GlRecurringRuns",
                column: "ReversalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringRuns_TemplateId_Occurrence",
                table: "GlRecurringRuns",
                columns: new[] { "TemplateId", "Occurrence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringTemplateLines_AccountId",
                table: "GlRecurringTemplateLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringTemplateLines_OrgUnitId",
                table: "GlRecurringTemplateLines",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringTemplateLines_ProjectId",
                table: "GlRecurringTemplateLines",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringTemplateLines_TemplateId",
                table: "GlRecurringTemplateLines",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringTemplateLines_WbsElementId",
                table: "GlRecurringTemplateLines",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_GlRecurringTemplates_Name",
                table: "GlRecurringTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyAccounts_CompanyId_CounterpartyCompanyId",
                table: "IntercompanyAccounts",
                columns: new[] { "CompanyId", "CounterpartyCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyAccounts_CounterpartyCompanyId",
                table: "IntercompanyAccounts",
                column: "CounterpartyCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyAccounts_DueFromAccountId",
                table: "IntercompanyAccounts",
                column: "DueFromAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_IntercompanyAccounts_DueToAccountId",
                table: "IntercompanyAccounts",
                column: "DueToAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborCostPolicy_EffectiveFrom",
                table: "LaborCostPolicy",
                column: "EffectiveFrom");

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceBasedPaymentEvent_ClinId",
                table: "PerformanceBasedPaymentEvent",
                column: "ClinId");

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceBasedPaymentEvent_ContractId_Order",
                table: "PerformanceBasedPaymentEvent",
                columns: new[] { "ContractId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceBasedPaymentEvent_InvoiceId",
                table: "PerformanceBasedPaymentEvent",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PieeSubmission_InvoiceId",
                table: "PieeSubmission",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProgressPaymentRequest_ContractId_Number",
                table: "ProgressPaymentRequest",
                columns: new[] { "ContractId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProgressPaymentRequest_InvoiceId",
                table: "ProgressPaymentRequest",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelAuthorization_EmployeeId_Status",
                table: "TravelAuthorization",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TravelAuthorization_ExpenseReportId",
                table: "TravelAuthorization",
                column: "ExpenseReportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelAuthorization_Number",
                table: "TravelAuthorization",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelAuthorization_ProjectId",
                table: "TravelAuthorization",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelAuthorization_WbsElementId",
                table: "TravelAuthorization",
                column: "WbsElementId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelAuthorizationDestination_TravelAuthorizationId_Order",
                table: "TravelAuthorizationDestination",
                columns: new[] { "TravelAuthorizationId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_TravelLineCategory_OdcEntryId",
                table: "TravelLineCategory",
                column: "OdcEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyAccess_CompanyId",
                table: "UserCompanyAccess",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyAccess_UserId_CompanyId",
                table: "UserCompanyAccess",
                columns: new[] { "UserId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanySelections_CompanyId",
                table: "UserCompanySelections",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanySelections_UserId",
                table: "UserCompanySelections",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ApBill_Companies_CompanyId",
                table: "ApBill",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApPaymentRun_Companies_CompanyId",
                table: "ApPaymentRun",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BankAccounts_Companies_CompanyId",
                table: "BankAccounts",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contract_Companies_CompanyId",
                table: "Contract",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Employee_Companies_CompanyId",
                table: "Employee",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FixedAsset_Companies_CompanyId",
                table: "FixedAsset",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GlAccounts_Companies_CompanyId",
                table: "GlAccounts",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GlJournalEntries_Companies_CompanyId",
                table: "GlJournalEntries",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GlJournalLines_Companies_CompanyId",
                table: "GlJournalLines",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GlJournalLines_Companies_IntercompanyCompanyId",
                table: "GlJournalLines",
                column: "IntercompanyCompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndirectPoolSetups_Companies_CompanyId",
                table: "IndirectPoolSetups",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoice_Companies_CompanyId",
                table: "Invoice",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payment_Companies_CompanyId",
                table: "Payment",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Project_Companies_CompanyId",
                table: "Project",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApBill_Companies_CompanyId",
                table: "ApBill");

            migrationBuilder.DropForeignKey(
                name: "FK_ApPaymentRun_Companies_CompanyId",
                table: "ApPaymentRun");

            migrationBuilder.DropForeignKey(
                name: "FK_BankAccounts_Companies_CompanyId",
                table: "BankAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Contract_Companies_CompanyId",
                table: "Contract");

            migrationBuilder.DropForeignKey(
                name: "FK_Employee_Companies_CompanyId",
                table: "Employee");

            migrationBuilder.DropForeignKey(
                name: "FK_FixedAsset_Companies_CompanyId",
                table: "FixedAsset");

            migrationBuilder.DropForeignKey(
                name: "FK_GlAccounts_Companies_CompanyId",
                table: "GlAccounts");

            migrationBuilder.DropForeignKey(
                name: "FK_GlJournalEntries_Companies_CompanyId",
                table: "GlJournalEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_GlJournalLines_Companies_CompanyId",
                table: "GlJournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_GlJournalLines_Companies_IntercompanyCompanyId",
                table: "GlJournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_IndirectPoolSetups_Companies_CompanyId",
                table: "IndirectPoolSetups");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoice_Companies_CompanyId",
                table: "Invoice");

            migrationBuilder.DropForeignKey(
                name: "FK_Payment_Companies_CompanyId",
                table: "Payment");

            migrationBuilder.DropForeignKey(
                name: "FK_Project_Companies_CompanyId",
                table: "Project");

            migrationBuilder.DropTable(
                name: "CardTransaction");

            migrationBuilder.DropTable(
                name: "CompanyPeriodCloses");

            migrationBuilder.DropTable(
                name: "ContractGovernmentBilling");

            migrationBuilder.DropTable(
                name: "Currencies");

            migrationBuilder.DropTable(
                name: "CurrencyRevaluationLines");

            migrationBuilder.DropTable(
                name: "CurrencySettings");

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "GlBudgetLines");

            migrationBuilder.DropTable(
                name: "GlBudgetSubmissionLines");

            migrationBuilder.DropTable(
                name: "GlRecurringRuns");

            migrationBuilder.DropTable(
                name: "GlRecurringSettings");

            migrationBuilder.DropTable(
                name: "GlRecurringTemplateLines");

            migrationBuilder.DropTable(
                name: "IntercompanyAccounts");

            migrationBuilder.DropTable(
                name: "LaborCostPolicy");

            migrationBuilder.DropTable(
                name: "PerformanceBasedPaymentEvent");

            migrationBuilder.DropTable(
                name: "PieeSubmission");

            migrationBuilder.DropTable(
                name: "ProgressPaymentRequest");

            migrationBuilder.DropTable(
                name: "TravelAuthorizationDestination");

            migrationBuilder.DropTable(
                name: "TravelLineCategory");

            migrationBuilder.DropTable(
                name: "UserCompanyAccess");

            migrationBuilder.DropTable(
                name: "UserCompanySelections");

            migrationBuilder.DropTable(
                name: "CardStatementImport");

            migrationBuilder.DropTable(
                name: "CorporateCard");

            migrationBuilder.DropTable(
                name: "CurrencyRevaluations");

            migrationBuilder.DropTable(
                name: "GlBudgetSubmissions");

            migrationBuilder.DropTable(
                name: "GlRecurringTemplates");

            migrationBuilder.DropTable(
                name: "TravelAuthorization");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "CardProgram");

            migrationBuilder.DropTable(
                name: "GlPlanningCycles");

            migrationBuilder.DropTable(
                name: "GlBudgetVersions");

            migrationBuilder.DropIndex(
                name: "IX_Project_CompanyId",
                table: "Project");

            migrationBuilder.DropIndex(
                name: "IX_Payment_CompanyId",
                table: "Payment");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_CompanyId",
                table: "Invoice");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_CurrencyCode",
                table: "Invoice");

            migrationBuilder.DropIndex(
                name: "IX_IndirectPoolSetups_CompanyId",
                table: "IndirectPoolSetups");

            migrationBuilder.DropIndex(
                name: "IX_GlJournalLines_CompanyId_AccountId",
                table: "GlJournalLines");

            migrationBuilder.DropIndex(
                name: "IX_GlJournalLines_IntercompanyCompanyId",
                table: "GlJournalLines");

            migrationBuilder.DropIndex(
                name: "IX_GlJournalEntries_CompanyId",
                table: "GlJournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_GlAccounts_CompanyId",
                table: "GlAccounts");

            migrationBuilder.DropIndex(
                name: "IX_FixedAsset_CompanyId",
                table: "FixedAsset");

            migrationBuilder.DropIndex(
                name: "IX_Employee_CompanyId",
                table: "Employee");

            migrationBuilder.DropIndex(
                name: "IX_Contract_CompanyId",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_BankAccounts_CompanyId",
                table: "BankAccounts");

            migrationBuilder.DropIndex(
                name: "IX_ApPaymentRun_CompanyId",
                table: "ApPaymentRun");

            migrationBuilder.DropIndex(
                name: "IX_ApBill_CompanyId",
                table: "ApBill");

            migrationBuilder.DropIndex(
                name: "IX_ApBill_CurrencyCode",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "Vendor");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "PaymentApplication");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "RealizedGainLoss",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "OdcEntry");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "OdcEntry");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "OdcEntry");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "TransactionCredited",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "TransactionPaid",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "TransactionTotal",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "IndirectPoolSetups");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "GlJournalLines");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "GlJournalLines");

            migrationBuilder.DropColumn(
                name: "IntercompanyCompanyId",
                table: "GlJournalLines");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "GlJournalLines");

            migrationBuilder.DropColumn(
                name: "TransactionCurrency",
                table: "GlJournalLines");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "GlJournalEntries");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "GlAccounts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "FixedAsset");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Employee");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "CustomerBillingProfile");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "BankAccounts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ApPaymentRun");

            migrationBuilder.DropColumn(
                name: "CashAmount",
                table: "ApPayment");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "ApPayment");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "ApPayment");

            migrationBuilder.DropColumn(
                name: "RealizedGainLoss",
                table: "ApPayment");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "ApPayment");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "ApBillLine");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "TransactionAmount",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "TransactionPaid",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "TransactionRetainage",
                table: "ApBill");
        }
    }
}
