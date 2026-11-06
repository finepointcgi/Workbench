using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddLeadFitAndCompanyProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FitByAssistant",
                table: "OpportunityLead",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FitReason",
                table: "OpportunityLead",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FitScore",
                table: "OpportunityLead",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FitScoredAtUtc",
                table: "OpportunityLead",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyProfile",
                table: "CrmSettings",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FitByAssistant",
                table: "OpportunityLead");

            migrationBuilder.DropColumn(
                name: "FitReason",
                table: "OpportunityLead");

            migrationBuilder.DropColumn(
                name: "FitScore",
                table: "OpportunityLead");

            migrationBuilder.DropColumn(
                name: "FitScoredAtUtc",
                table: "OpportunityLead");

            migrationBuilder.DropColumn(
                name: "CompanyProfile",
                table: "CrmSettings");
        }
    }
}
