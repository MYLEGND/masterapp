using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260517183000_AddMetaSignalPageModeIndex")]
public partial class AddMetaSignalPageModeIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_MetaSignalEvents_PageMode",
            table: "MetaSignalEvents",
            column: "PageMode");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MetaSignalEvents_PageMode",
            table: "MetaSignalEvents");
    }
}
