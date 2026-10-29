using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class AddCalendarFeedAndTeamsNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarFeedToken",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TokenProtected = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastFetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarFeedToken", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatChannel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    WebhookUrlProtected = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatChannel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatNotificationChoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ChannelKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatNotificationChoice", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedToken_TokenHash",
                table: "CalendarFeedToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarFeedToken_UserEmail",
                table: "CalendarFeedToken",
                column: "UserEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannel_Kind",
                table: "ChatChannel",
                column: "Kind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatNotificationChoice_UserEmail_ChannelKind_Group",
                table: "ChatNotificationChoice",
                columns: new[] { "UserEmail", "ChannelKind", "Group" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarFeedToken");

            migrationBuilder.DropTable(
                name: "ChatChannel");

            migrationBuilder.DropTable(
                name: "ChatNotificationChoice");
        }
    }
}
