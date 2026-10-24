using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, collation: "C"),
                    FirstAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ClearedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClearedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C")
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
