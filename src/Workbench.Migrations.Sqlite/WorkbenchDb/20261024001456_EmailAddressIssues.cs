using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
{
    /// <inheritdoc />
    public partial class EmailAddressIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailAddressIssue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FirstAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClearedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClearedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAddressIssue", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailAddressIssue_Address",
                table: "EmailAddressIssue",
                column: "Address",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailAddressIssue");
        }
    }
}
