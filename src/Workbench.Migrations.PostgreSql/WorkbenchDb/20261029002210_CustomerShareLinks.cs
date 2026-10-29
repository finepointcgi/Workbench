using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class CustomerShareLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerShareLink",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    LastViewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ViewCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerShareLink", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerShareLink_Contract_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contract",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerShareLink_ContractId",
                table: "CustomerShareLink",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerShareLink_TokenHash",
                table: "CustomerShareLink",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerShareLink");
        }
    }
}
