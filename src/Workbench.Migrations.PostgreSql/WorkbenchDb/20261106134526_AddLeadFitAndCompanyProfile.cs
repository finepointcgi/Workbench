using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FitReason",
                table: "OpportunityLead",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<int>(
                name: "FitScore",
                table: "OpportunityLead",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FitScoredAtUtc",
                table: "OpportunityLead",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyProfile",
                table: "CrmSettings",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true,
                collation: "C");
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
