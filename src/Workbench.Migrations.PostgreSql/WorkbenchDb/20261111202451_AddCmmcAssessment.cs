using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddCmmcAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CmmcPoamItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PracticeId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Milestone = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmmcPoamItem", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CmmcPractice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PracticeId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PartialCredit = table.Column<bool>(type: "boolean", nullable: false),
                    Implementation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    AssessedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmmcPractice", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CmmcPoamItem_PracticeId",
                table: "CmmcPoamItem",
                column: "PracticeId");

            migrationBuilder.CreateIndex(
                name: "IX_CmmcPractice_PracticeId",
                table: "CmmcPractice",
                column: "PracticeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CmmcPoamItem");

            migrationBuilder.DropTable(
                name: "CmmcPractice");
        }
    }
}
