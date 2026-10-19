using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class MilestonesPmRoleAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsProjectManager",
                table: "UserInvite",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMilestone",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsProjectManager",
                table: "AppUser",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ItemAssignee",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    AssignedBy = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemAssignee", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemAssignee_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemAssignee_Email",
                table: "ItemAssignee",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_ItemAssignee_ItemId_Email",
                table: "ItemAssignee",
                columns: new[] { "ItemId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemAssignee_SheetId",
                table: "ItemAssignee",
                column: "SheetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemAssignee");

            migrationBuilder.DropColumn(
                name: "IsProjectManager",
                table: "UserInvite");

            migrationBuilder.DropColumn(
                name: "IsMilestone",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "IsProjectManager",
                table: "AppUser");
        }
    }
}
