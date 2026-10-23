using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class SheetViewsAndStatusColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OptionColors",
                table: "Columns",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "SheetView",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    IsShared = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSheetDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", maxLength: 50000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetView", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SheetView_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SheetViewPreference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ViewId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetViewPreference", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SheetViewPreference_SheetView_ViewId",
                        column: x => x.ViewId,
                        principalTable: "SheetView",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SheetViewPreference_Sheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "Sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SheetView_SheetId_OwnerEmail",
                table: "SheetView",
                columns: new[] { "SheetId", "OwnerEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_SheetViewPreference_SheetId",
                table: "SheetViewPreference",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetViewPreference_UserEmail_SheetId",
                table: "SheetViewPreference",
                columns: new[] { "UserEmail", "SheetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SheetViewPreference_ViewId",
                table: "SheetViewPreference",
                column: "ViewId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SheetViewPreference");

            migrationBuilder.DropTable(
                name: "SheetView");

            migrationBuilder.DropColumn(
                name: "OptionColors",
                table: "Columns");
        }
    }
}
