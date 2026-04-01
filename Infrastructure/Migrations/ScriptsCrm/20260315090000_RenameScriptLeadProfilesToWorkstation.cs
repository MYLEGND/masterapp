using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations.ScriptsCrm
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260315090000_RenameScriptLeadProfilesToWorkstation")]
    public partial class RenameScriptLeadProfilesToWorkstation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Sqlite", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
ALTER TABLE ""ScriptLeadProfiles"" RENAME TO ""WorkstationLeadProfiles"";
");
                return;
            }

            // SQL Server idempotent rename: skip if already renamed
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[WorkstationLeadProfiles]', 'U') IS NOT NULL
    RETURN;

IF OBJECT_ID(N'[ScriptLeadProfiles]', 'U') IS NULL
    RETURN;

DECLARE @pk sysname;
SELECT @pk = kc.name
FROM sys.key_constraints kc
JOIN sys.tables t ON kc.parent_object_id = t.object_id
WHERE kc.[type] = 'PK' AND t.[name] = 'ScriptLeadProfiles';

IF @pk IS NOT NULL
    EXEC('ALTER TABLE [ScriptLeadProfiles] DROP CONSTRAINT [' + @pk + ']');

EXEC sp_rename 'ScriptLeadProfiles', 'WorkstationLeadProfiles';

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_OriginalLeadType')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_OriginalLeadType', 'IX_WorkstationLeadProfiles_OriginalLeadType', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_Phone')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_Phone', 'IX_WorkstationLeadProfiles_Phone', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_Email')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_Email', 'IX_WorkstationLeadProfiles_Email', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_Bucket')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_Bucket', 'IX_WorkstationLeadProfiles_Bucket', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_AgentUserId_Phone')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_AgentUserId_Phone', 'IX_WorkstationLeadProfiles_AgentUserId_Phone', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_AgentUserId_OriginalLeadType')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_AgentUserId_OriginalLeadType', 'IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScriptLeadProfiles_AgentUserId')
    EXEC sp_rename 'WorkstationLeadProfiles.IX_ScriptLeadProfiles_AgentUserId', 'IX_WorkstationLeadProfiles_AgentUserId', 'INDEX';

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_WorkstationLeadProfiles')
    ALTER TABLE [WorkstationLeadProfiles] ADD CONSTRAINT [PK_WorkstationLeadProfiles] PRIMARY KEY ([LeadId]);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Sqlite", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
ALTER TABLE ""WorkstationLeadProfiles"" RENAME TO ""ScriptLeadProfiles"";
");
                return;
            }

            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[ScriptLeadProfiles]', 'U') IS NOT NULL
    RETURN;

IF OBJECT_ID(N'[WorkstationLeadProfiles]', 'U') IS NULL
    RETURN;

DECLARE @pk sysname;
SELECT @pk = kc.name
FROM sys.key_constraints kc
JOIN sys.tables t ON kc.parent_object_id = t.object_id
WHERE kc.[type] = 'PK' AND t.[name] = 'WorkstationLeadProfiles';

IF @pk IS NOT NULL
    EXEC('ALTER TABLE [WorkstationLeadProfiles] DROP CONSTRAINT [' + @pk + ']');

EXEC sp_rename 'WorkstationLeadProfiles', 'ScriptLeadProfiles';

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_OriginalLeadType')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_OriginalLeadType', 'IX_ScriptLeadProfiles_OriginalLeadType', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_Phone')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_Phone', 'IX_ScriptLeadProfiles_Phone', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_Email')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_Email', 'IX_ScriptLeadProfiles_Email', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_Bucket')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_Bucket', 'IX_ScriptLeadProfiles_Bucket', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_AgentUserId_Phone')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_AgentUserId_Phone', 'IX_ScriptLeadProfiles_AgentUserId_Phone', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_AgentUserId_OriginalLeadType', 'IX_ScriptLeadProfiles_AgentUserId_OriginalLeadType', 'INDEX';
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_WorkstationLeadProfiles_AgentUserId')
    EXEC sp_rename 'ScriptLeadProfiles.IX_WorkstationLeadProfiles_AgentUserId', 'IX_ScriptLeadProfiles_AgentUserId', 'INDEX';

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_ScriptLeadProfiles')
    ALTER TABLE [ScriptLeadProfiles] ADD CONSTRAINT [PK_ScriptLeadProfiles] PRIMARY KEY ([LeadId]);
");
        }
    }
}
