using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260927090000_AddOpenAiProductFeedProjections")]
public partial class AddOpenAiProductFeedProjections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OpenAiProductFeedProjections",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                CommerceBusinessId = table.Column<Guid>(nullable: false),
                CommerceProductId = table.Column<Guid>(nullable: false),
                Provider = table.Column<string>(maxLength: 20, nullable: false),
                ProviderFeedId = table.Column<string>(maxLength: 200, nullable: true),
                ProviderProductId = table.Column<string>(maxLength: 200, nullable: true),
                Status = table.Column<string>(maxLength: 32, nullable: false),
                CanonicalFingerprint = table.Column<string>(maxLength: 64, nullable: false),
                LastError = table.Column<string>(maxLength: 2000, nullable: true),
                LastPublishedUtc = table.Column<DateTime>(nullable: true),
                UpdatedUtc = table.Column<DateTime>(nullable: false),
                Revision = table.Column<Guid>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenAiProductFeedProjections", x => x.Id);
                table.ForeignKey(
                    name: "FK_OpenAiProductFeedProjections_CommerceBusinesses_CommerceBusinessId",
                    column: x => x.CommerceBusinessId,
                    principalTable: "CommerceBusinesses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_OpenAiProductFeedProjections_CommerceProducts_CommerceProductId",
                    column: x => x.CommerceProductId,
                    principalTable: "CommerceProducts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OpenAiProductFeedProjections_CommerceBusinessId_CommerceProductId_Provider",
            table: "OpenAiProductFeedProjections",
            columns: new[] { "CommerceBusinessId", "CommerceProductId", "Provider" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OpenAiProductFeedProjections_CommerceBusinessId_ProviderFeedId",
            table: "OpenAiProductFeedProjections",
            columns: new[] { "CommerceBusinessId", "ProviderFeedId" });

        migrationBuilder.CreateIndex(
            name: "IX_OpenAiProductFeedProjections_CommerceProductId",
            table: "OpenAiProductFeedProjections",
            column: "CommerceProductId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "OpenAiProductFeedProjections");
}
