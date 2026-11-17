using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddOrganizationProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ShowGovernmentFeatures = table.Column<bool>(type: "INTEGER", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationProfile", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationProfile");
        }
    }
}
