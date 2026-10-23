using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Migrations.Sqlite.WorkbenchDb
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FormId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ClientKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                    RejectedReason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormSubmission", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetForm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Fields = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmationMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    Audience = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PublicToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NotifyOnSubmit = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetForm", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetReference",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceSheetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fields = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetReference", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetReport",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OwnerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SheetIds = table.Column<string>(type: "TEXT", nullable: false),
                    Columns = table.Column<string>(type: "TEXT", nullable: false),
                    Filters = table.Column<string>(type: "TEXT", nullable: false),
                    MatchAll = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SortDescending = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SheetReport", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SheetTemplate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false)
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
