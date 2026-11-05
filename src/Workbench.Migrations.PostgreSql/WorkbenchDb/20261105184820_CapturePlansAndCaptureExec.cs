using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateTable(
                name: "OpportunityMilestone",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true, collation: "C"),
                    IsDone = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, collation: "C"),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerRelationship = table.Column<int>(type: "integer", nullable: false),
                    SolutionFit = table.Column<int>(type: "integer", nullable: false),
                    PastPerformance = table.Column<int>(type: "integer", nullable: false),
                    PriceToWin = table.Column<int>(type: "integer", nullable: false),
                    CompetitivePosition = table.Column<int>(type: "integer", nullable: false),
                    Teaming = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    AssessedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    AssessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
