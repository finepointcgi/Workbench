using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class StaffingAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignmentId",
                table: "ResourceAllocation",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffingAssignment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WbsElementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LaborCategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    From = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    To = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TotalHours = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffingAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffingAssignment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffingAssignment_LaborCategory_LaborCategoryId",
                        column: x => x.LaborCategoryId,
                        principalTable: "LaborCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffingAssignment_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffingAssignment_WbsElement_WbsElementId",
                        column: x => x.WbsElementId,
                        principalTable: "WbsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceAllocation_AssignmentId",
                table: "ResourceAllocation",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffingAssignment_EmployeeId_To",
                table: "StaffingAssignment",
                columns: new[] { "EmployeeId", "To" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingAssignment_LaborCategoryId",
                table: "StaffingAssignment",
                column: "LaborCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffingAssignment_ProjectId_EmployeeId",
                table: "StaffingAssignment",
                columns: new[] { "ProjectId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingAssignment_WbsElementId",
                table: "StaffingAssignment",
                column: "WbsElementId");

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceAllocation_StaffingAssignment_AssignmentId",
                table: "ResourceAllocation",
                column: "AssignmentId",
                principalTable: "StaffingAssignment",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceAllocation_StaffingAssignment_AssignmentId",
                table: "ResourceAllocation");

            migrationBuilder.DropTable(
                name: "StaffingAssignment");

            migrationBuilder.DropIndex(
                name: "IX_ResourceAllocation_AssignmentId",
                table: "ResourceAllocation");

            migrationBuilder.DropColumn(
                name: "AssignmentId",
                table: "ResourceAllocation");
        }
    }
}
