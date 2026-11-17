using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountWrittenOff",
                table: "Invoice",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "AgreementBillingPlan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Cadence = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    InvoiceOnComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludedHours = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: true),
                    UnusedHours = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OverageRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    AutoGenerate = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CompletedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CompletedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LegalName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RemitToAddress = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RemittanceInstructions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PaymentTermsText = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DefaultPaymentTermsDays = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceFooter = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    BillingContact = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ReminderDaysBeforeDue = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultLayout = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingOrganizationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceWriteOff",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-00000000c001")),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WrittenOffOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsReversed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReversalReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReversedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
