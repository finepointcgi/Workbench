using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReminderLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SentOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReminderPreference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    TimesheetEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ViaEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    ViaTeams = table.Column<bool>(type: "INTEGER", nullable: false),
                    QuietStartHour = table.Column<int>(type: "INTEGER", nullable: true),
                    QuietEndHour = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderPreference", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLog_UserEmail_Kind_Key",
                table: "ReminderLog",
                columns: new[] { "UserEmail", "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLog_UserEmail_Kind_SentOn",
                table: "ReminderLog",
                columns: new[] { "UserEmail", "Kind", "SentOn" });

            migrationBuilder.CreateIndex(
                name: "IX_ReminderPreference_UserEmail",
                table: "ReminderPreference",
                column: "UserEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderLog");

            migrationBuilder.DropTable(
                name: "ReminderPreference");
        }
    }
}
