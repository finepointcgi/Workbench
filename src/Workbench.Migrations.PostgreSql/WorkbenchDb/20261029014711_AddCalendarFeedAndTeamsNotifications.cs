using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    TokenProtected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarFeedToken", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatChannel",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    WebhookUrlProtected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatChannel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatNotificationChoice",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    ChannelKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    Group = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C")
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
