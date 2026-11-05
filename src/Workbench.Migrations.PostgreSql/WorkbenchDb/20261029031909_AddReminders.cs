using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    SentOn = table.Column<DateOnly>(type: "date", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReminderPreference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    TimesheetEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ViaEmail = table.Column<bool>(type: "boolean", nullable: false),
                    ViaTeams = table.Column<bool>(type: "boolean", nullable: false),
                    QuietStartHour = table.Column<int>(type: "integer", nullable: true),
                    QuietEndHour = table.Column<int>(type: "integer", nullable: true)
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
