using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260925050000_AddWebsiteStudioComments")]
public partial class AddWebsiteStudioComments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WebsiteStudioComments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WebsiteContentStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WebsiteContentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AnchorRevision = table.Column<long>(type: "bigint", nullable: false),
                PagePath = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                ElementId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                ParentCommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                AuthorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                AuthorEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                AuthorRole = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                ResolvedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebsiteStudioComments", x => x.Id);
                table.ForeignKey(
                    name: "FK_WebsiteStudioComments_WebsiteContentState_WebsiteContentStateId",
                    column: x => x.WebsiteContentStateId,
                    principalTable: "WebsiteContentState",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_WebsiteStudioComments_WebsiteContentVersion_WebsiteContentVersionId",
                    column: x => x.WebsiteContentVersionId,
                    principalTable: "WebsiteContentVersion",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_WebsiteStudioComments_WebsiteStudioComments_ParentCommentId",
                    column: x => x.ParentCommentId,
                    principalTable: "WebsiteStudioComments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteStudioComments_WebsiteContentStateId_PagePath_Status_CreatedUtc",
            table: "WebsiteStudioComments",
            columns: new[] { "WebsiteContentStateId", "PagePath", "Status", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteStudioComments_WebsiteContentStateId_ElementId_Status",
            table: "WebsiteStudioComments",
            columns: new[] { "WebsiteContentStateId", "ElementId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteStudioComments_ParentCommentId",
            table: "WebsiteStudioComments",
            column: "ParentCommentId");

        migrationBuilder.CreateIndex(
            name: "IX_WebsiteStudioComments_WebsiteContentVersionId",
            table: "WebsiteStudioComments",
            column: "WebsiteContentVersionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "WebsiteStudioComments");
    }
}
