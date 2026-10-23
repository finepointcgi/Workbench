using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class PlanCalendarsAndConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanCalendar",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UseCompanyHolidays = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanCalendar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanCalendar_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskConstraint",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskConstraint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskConstraint_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskConstraint_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanCalendarException",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsWorking = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanCalendarException", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanCalendarException_PlanCalendar_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "PlanCalendar",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanCalendar_SheetId",
                table: "PlanCalendar",
                column: "SheetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanCalendarException_CalendarId_Date",
                table: "PlanCalendarException",
                columns: new[] { "CalendarId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskConstraint_ItemId",
                table: "TaskConstraint",
                column: "ItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskConstraint_SheetId",
                table: "TaskConstraint",
                column: "SheetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanCalendarException");

            migrationBuilder.DropTable(
                name: "TaskConstraint");

            migrationBuilder.DropTable(
                name: "PlanCalendar");
        }
    }
}
