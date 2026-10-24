using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddApCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreditsLineId",
                table: "ApBillLine",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReleasesBillId",
                table: "ApBillLine",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalOdcEntryId",
                table: "ApBillLine",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AppliedAmount",
                table: "ApBill",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "CreditsBillId",
                table: "ApBill",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ApBill",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Bill");

            migrationBuilder.AddColumn<Guid>(
                name: "QualityDebitMemoId",
                table: "ApBill",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "VoidReversalDate",
                table: "ApBill",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAtUtc",
                table: "ApBill",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                table: "ApBill",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "ApCreditApplication",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AppliedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReversedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReversedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApCreditApplication", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApCreditApplication_ApBill_BillId",
                        column: x => x.BillId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApCreditApplication_ApBill_CreditId",
                        column: x => x.CreditId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApCreditApplication_ApPaymentRun_RunId",
                        column: x => x.RunId,
                        principalTable: "ApPaymentRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApCreditApplication_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApRefund",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditId = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    VoidedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    VoidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApRefund", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApRefund_ApBill_CreditId",
                        column: x => x.CreditId,
                        principalTable: "ApBill",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApRefund_GlJournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "GlJournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApRefund_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_CreditsLineId",
                table: "ApBillLine",
                column: "CreditsLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_ReleasesBillId",
                table: "ApBillLine",
                column: "ReleasesBillId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBillLine_ReversalOdcEntryId",
                table: "ApBillLine",
                column: "ReversalOdcEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_CreditsBillId",
                table: "ApBill",
                column: "CreditsBillId");

            migrationBuilder.CreateIndex(
                name: "IX_ApBill_VendorId_Kind_Status",
                table: "ApBill",
                columns: new[] { "VendorId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApCreditApplication_BillId",
                table: "ApCreditApplication",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_ApCreditApplication_CreditId",
                table: "ApCreditApplication",
                column: "CreditId");

            migrationBuilder.CreateIndex(
                name: "IX_ApCreditApplication_RunId",
                table: "ApCreditApplication",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ApCreditApplication_VendorId",
                table: "ApCreditApplication",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_ApRefund_CreditId",
                table: "ApRefund",
                column: "CreditId");

            migrationBuilder.CreateIndex(
                name: "IX_ApRefund_JournalEntryId",
                table: "ApRefund",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApRefund_VendorId_ReceivedOn",
                table: "ApRefund",
                columns: new[] { "VendorId", "ReceivedOn" });

            migrationBuilder.AddForeignKey(
                name: "FK_ApBill_ApBill_CreditsBillId",
                table: "ApBill",
                column: "CreditsBillId",
                principalTable: "ApBill",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApBillLine_ApBillLine_CreditsLineId",
                table: "ApBillLine",
                column: "CreditsLineId",
                principalTable: "ApBillLine",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApBillLine_ApBill_ReleasesBillId",
                table: "ApBillLine",
                column: "ReleasesBillId",
                principalTable: "ApBill",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApBillLine_OdcEntry_ReversalOdcEntryId",
                table: "ApBillLine",
                column: "ReversalOdcEntryId",
                principalTable: "OdcEntry",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApBill_ApBill_CreditsBillId",
                table: "ApBill");

            migrationBuilder.DropForeignKey(
                name: "FK_ApBillLine_ApBillLine_CreditsLineId",
                table: "ApBillLine");

            migrationBuilder.DropForeignKey(
                name: "FK_ApBillLine_ApBill_ReleasesBillId",
                table: "ApBillLine");

            migrationBuilder.DropForeignKey(
                name: "FK_ApBillLine_OdcEntry_ReversalOdcEntryId",
                table: "ApBillLine");

            migrationBuilder.DropTable(
                name: "ApCreditApplication");

            migrationBuilder.DropTable(
                name: "ApRefund");

            migrationBuilder.DropIndex(
                name: "IX_ApBillLine_CreditsLineId",
                table: "ApBillLine");

            migrationBuilder.DropIndex(
                name: "IX_ApBillLine_ReleasesBillId",
                table: "ApBillLine");

            migrationBuilder.DropIndex(
                name: "IX_ApBillLine_ReversalOdcEntryId",
                table: "ApBillLine");

            migrationBuilder.DropIndex(
                name: "IX_ApBill_CreditsBillId",
                table: "ApBill");

            migrationBuilder.DropIndex(
                name: "IX_ApBill_VendorId_Kind_Status",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "CreditsLineId",
                table: "ApBillLine");

            migrationBuilder.DropColumn(
                name: "ReleasesBillId",
                table: "ApBillLine");

            migrationBuilder.DropColumn(
                name: "ReversalOdcEntryId",
                table: "ApBillLine");

            migrationBuilder.DropColumn(
                name: "AppliedAmount",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "CreditsBillId",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "QualityDebitMemoId",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "VoidReversalDate",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "VoidedAtUtc",
                table: "ApBill");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                table: "ApBill");
        }
    }
}
