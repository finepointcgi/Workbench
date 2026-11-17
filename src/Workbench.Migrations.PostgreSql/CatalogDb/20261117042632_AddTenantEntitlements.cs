using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.CatalogDb
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
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Modules = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    LightSeats = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsTrial = table.Column<bool>(type: "boolean", nullable: false),
                    TrialEndsUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidThroughUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PastDueSinceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    ExternalRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    SourceEventAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SignupEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    SignupSource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    TermsAcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TermsVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true, collation: "C"),
                    CompanyType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C")
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
