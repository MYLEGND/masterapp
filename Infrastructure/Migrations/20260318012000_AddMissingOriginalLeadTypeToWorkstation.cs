using System;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(MasterAppDbContext))]
[Migration("20260318012000_AddMissingOriginalLeadTypeToWorkstation")]
public partial class AddMissingOriginalLeadTypeToWorkstation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OriginalLeadType",
            table: "WorkstationLeadProfiles",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE \"WorkstationLeadProfiles\" SET \"OriginalLeadType\" = \"Bucket\" WHERE \"OriginalLeadType\" IS NULL OR TRIM(\"OriginalLeadType\") = '';"
        );

        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_WorkstationLeadProfiles_OriginalLeadType\" ON \"WorkstationLeadProfiles\" (\"OriginalLeadType\");");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType\" ON \"WorkstationLeadProfiles\" (\"AgentUserId\", \"OriginalLeadType\");");
        }
        else
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_OriginalLeadType')
    CREATE INDEX [IX_WorkstationLeadProfiles_OriginalLeadType] ON [WorkstationLeadProfiles]([OriginalLeadType]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType')
    CREATE INDEX [IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType] ON [WorkstationLeadProfiles]([AgentUserId], [OriginalLeadType]);
");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropIndex(
            name: "IX_WorkstationLeadProfiles_OriginalLeadType",
            table: "WorkstationLeadProfiles");

        migrationBuilder.DropColumn(
            name: "OriginalLeadType",
            table: "WorkstationLeadProfiles");
    }
}
