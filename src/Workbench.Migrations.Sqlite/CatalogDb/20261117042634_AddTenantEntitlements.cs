using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.CatalogDb
{
    /// <inheritdoc />
    public partial class AddTenantEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantEntitlements",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Plan = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Modules = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Seats = table.Column<int>(type: "INTEGER", nullable: false),
                    LightSeats = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    IsTrial = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrialEndsUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidThroughUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PastDueSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SourceEventAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    SignupEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    SignupSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TermsAcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TermsVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CompanyType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantEntitlements", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_TenantEntitlements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlements_SignupEmail",
                table: "TenantEntitlements",
                column: "SignupEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantEntitlements");
        }
    }
}
