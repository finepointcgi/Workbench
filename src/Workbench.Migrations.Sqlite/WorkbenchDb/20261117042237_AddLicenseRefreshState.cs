using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InstallId",
                table: "LicenseSettings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRefreshAttemptUtc",
                table: "LicenseSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRefreshMessage",
                table: "LicenseSettings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRefreshOutcome",
                table: "LicenseSettings",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRefreshSuccessUtc",
                table: "LicenseSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerLicenseId",
                table: "LicenseSettings",
                type: "TEXT",
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
