using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class CapturePlansAndCaptureExec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "Opportunity",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OpportunityMilestone",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DueOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityMilestone", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityMilestone_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityPlanItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityPlanItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityPlanItem_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityPwinAssessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerRelationship = table.Column<int>(type: "INTEGER", nullable: false),
                    SolutionFit = table.Column<int>(type: "INTEGER", nullable: false),
                    PastPerformance = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceToWin = table.Column<int>(type: "INTEGER", nullable: false),
                    CompetitivePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    Teaming = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AssessedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    AssessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityPwinAssessment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityPwinAssessment_Opportunity_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunity_Source_SourceId",
                table: "Opportunity",
                columns: new[] { "Source", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityMilestone_IsDone_DueOn",
                table: "OpportunityMilestone",
                columns: new[] { "IsDone", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityMilestone_OpportunityId",
                table: "OpportunityMilestone",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityPlanItem_OpportunityId",
                table: "OpportunityPlanItem",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityPwinAssessment_OpportunityId",
                table: "OpportunityPwinAssessment",
                column: "OpportunityId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpportunityMilestone");

            migrationBuilder.DropTable(
                name: "OpportunityPlanItem");

            migrationBuilder.DropTable(
                name: "OpportunityPwinAssessment");

            migrationBuilder.DropIndex(
                name: "IX_Opportunity_Source_SourceId",
                table: "Opportunity");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "Opportunity");
        }
    }
}
