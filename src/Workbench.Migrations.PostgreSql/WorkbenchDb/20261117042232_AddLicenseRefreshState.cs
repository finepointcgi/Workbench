using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddLicenseRefreshState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveRefreshFailures",
                table: "LicenseSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InstallId",
                table: "LicenseSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRefreshAttemptUtc",
                table: "LicenseSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRefreshMessage",
                table: "LicenseSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "LastRefreshOutcome",
                table: "LicenseSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRefreshSuccessUtc",
                table: "LicenseSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerLicenseId",
                table: "LicenseSettings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveRefreshFailures",
                table: "LicenseSettings");

            migrationBuilder.DropColumn(
                name: "InstallId",
                table: "LicenseSettings");

            migrationBuilder.DropColumn(
                name: "LastRefreshAttemptUtc",
                table: "LicenseSettings");

            migrationBuilder.DropColumn(
                name: "LastRefreshMessage",
                table: "LicenseSettings");

            migrationBuilder.DropColumn(
                name: "LastRefreshOutcome",
                table: "LicenseSettings");

            migrationBuilder.DropColumn(
                name: "LastRefreshSuccessUtc",
                table: "LicenseSettings");

            migrationBuilder.DropColumn(
                name: "ServerLicenseId",
                table: "LicenseSettings");
        }
    }
}
