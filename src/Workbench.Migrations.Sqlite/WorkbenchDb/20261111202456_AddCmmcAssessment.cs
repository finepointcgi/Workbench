using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PracticeId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Milestone = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    DueOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmmcPoamItem", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CmmcPractice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PracticeId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PartialCredit = table.Column<bool>(type: "INTEGER", nullable: false),
                    Implementation = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    AssessedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
