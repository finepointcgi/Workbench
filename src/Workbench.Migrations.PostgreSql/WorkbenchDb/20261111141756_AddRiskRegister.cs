using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProbabilityPercent = table.Column<int>(type: "integer", nullable: false),
                    CostImpact = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ScheduleImpactDays = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Response = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResponsePlan = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ReviewOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PartRefs = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    RequirementRefs = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
