using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddCommercialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "UnappliedAmount",
                table: "Payment",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountWrittenOff",
                table: "Invoice",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "AgreementBillingPlan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Model = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Cadence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    InvoiceOnComplete = table.Column<bool>(type: "boolean", nullable: false),
                    IncludedHours = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    UnusedHours = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OverageRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    AutoGenerate = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgreementBillingPlan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgreementBillingPlan_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgreementMilestone",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgreementMilestone", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgreementMilestone_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgreementMilestone_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillingOrganizationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    RemitToAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    RemittanceInstructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    PaymentTermsText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    DefaultPaymentTermsDays = table.Column<int>(type: "integer", nullable: false),
                    InvoiceFooter = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    BillingContact = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    ReminderDaysBeforeDue = table.Column<int>(type: "integer", nullable: false),
                    DefaultLayout = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingOrganizationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceWriteOff",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-00000000c001")),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WrittenOffOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, collation: "C"),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsReversed = table.Column<bool>(type: "boolean", nullable: false),
                    ReversalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    ReversedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceWriteOff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceWriteOff_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceWriteOff_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceWriteOff_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceWriteOff_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgreementBillingPlan_ContractId",
                table: "AgreementBillingPlan",
                column: "ContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgreementMilestone_ContractId",
                table: "AgreementMilestone",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_AgreementMilestone_InvoiceId",
                table: "AgreementMilestone",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceWriteOff_CompanyId",
                table: "InvoiceWriteOff",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceWriteOff_CustomerId_WrittenOffOn",
                table: "InvoiceWriteOff",
                columns: new[] { "CustomerId", "WrittenOffOn" });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceWriteOff_InvoiceId",
                table: "InvoiceWriteOff",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceWriteOff_JournalEntryId",
                table: "InvoiceWriteOff",
                column: "JournalEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgreementBillingPlan");

            migrationBuilder.DropTable(
                name: "AgreementMilestone");

            migrationBuilder.DropTable(
                name: "BillingOrganizationSettings");

            migrationBuilder.DropTable(
                name: "InvoiceWriteOff");

            migrationBuilder.DropColumn(
                name: "UnappliedAmount",
                table: "Payment");

            migrationBuilder.DropColumn(
                name: "AmountWrittenOff",
                table: "Invoice");
        }
    }
}
