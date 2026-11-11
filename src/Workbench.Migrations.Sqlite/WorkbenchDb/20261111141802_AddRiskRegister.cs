using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddRiskRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectRisk",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ProbabilityPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    CostImpact = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ScheduleImpactDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Response = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ResponsePlan = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ReviewOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PartRefs = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RequirementRefs = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRisk", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectRisk_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRisk_ProjectId_Number",
                table: "ProjectRisk",
                columns: new[] { "ProjectId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectRisk");
        }
    }
}
