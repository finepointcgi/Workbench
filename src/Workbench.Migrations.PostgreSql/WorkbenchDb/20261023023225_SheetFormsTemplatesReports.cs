using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.PostgreSql.WorkbenchDb
{
    /// <inheritdoc />
    public partial class SheetFormsTemplatesReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FormSubmission",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormId = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    ClientKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    RejectedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormSubmission", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetForm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    Fields = table.Column<string>(type: "text", nullable: false),
                    ConfirmationMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    Audience = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    NotifyOnSubmit = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetForm", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetReference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    SourceSheetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fields = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetReference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetReport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    OwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, collation: "C"),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SheetIds = table.Column<string>(type: "text", nullable: false),
                    Columns = table.Column<string>(type: "text", nullable: false),
                    Filters = table.Column<string>(type: "text", nullable: false),
                    MatchAll = table.Column<bool>(type: "boolean", nullable: false),
                    SortBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, collation: "C"),
                    SortDescending = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetReport", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetTemplate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true, collation: "C"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetTemplate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FormSubmission_FormId_SubmittedAtUtc",
                table: "FormSubmission",
                columns: new[] { "FormId", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SheetForm_PublicToken",
                table: "SheetForm",
                column: "PublicToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SheetForm_SheetId",
                table: "SheetForm",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetReference_SheetId",
                table: "SheetReference",
                column: "SheetId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetReference_SourceSheetId",
                table: "SheetReference",
                column: "SourceSheetId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetReport_OwnerEmail",
                table: "SheetReport",
                column: "OwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_SheetReport_WorkspaceId",
                table: "SheetReport",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_SheetTemplate_WorkspaceId",
                table: "SheetTemplate",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FormSubmission");

            migrationBuilder.DropTable(
                name: "SheetForm");

            migrationBuilder.DropTable(
                name: "SheetReference");

            migrationBuilder.DropTable(
                name: "SheetReport");

            migrationBuilder.DropTable(
                name: "SheetTemplate");
        }
    }
}
