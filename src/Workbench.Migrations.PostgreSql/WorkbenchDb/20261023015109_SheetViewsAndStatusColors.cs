using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class SheetViewsAndStatusColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "OptionColors",
                table: "Columns",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]"); // Existing columns start with no colors (suggested from their labels).

            migrationBuilder.CreateTable(
                name: "SheetView",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    IsSheetDefault = table.Column<bool>(type: "boolean", nullable: false),
                    DefinitionJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewId = table.Column<Guid>(type: "uuid", nullable: false)
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
