using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    [DbContext(typeof(MasterAppDbContext))]
    [Migration("20260724085008_RepairMessagingRowVersions")]
    public partial class RepairMessagingRowVersions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
            {
                return;
            }

            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
            {
                return;
            }

            migrationBuilder.Sql(
                @"
IF COL_LENGTH(N'dbo.MessageConversations', N'RowVersion') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.columns AS c
       INNER JOIN sys.tables AS t
           ON t.object_id = c.object_id
       INNER JOIN sys.schemas AS s
           ON s.schema_id = t.schema_id
       INNER JOIN sys.types AS ty
           ON ty.user_type_id = c.user_type_id
       WHERE s.name = N'dbo'
         AND t.name = N'MessageConversations'
         AND c.name = N'RowVersion'
         AND ty.name <> N'timestamp'
   )
BEGIN
    ALTER TABLE [dbo].[MessageConversations]
        DROP COLUMN [RowVersion];

    ALTER TABLE [dbo].[MessageConversations]
        ADD [RowVersion] rowversion NOT NULL;
END;
");

            migrationBuilder.Sql(
                @"
IF COL_LENGTH(N'dbo.InternalMessages', N'RowVersion') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.columns AS c
       INNER JOIN sys.tables AS t
           ON t.object_id = c.object_id
       INNER JOIN sys.schemas AS s
           ON s.schema_id = t.schema_id
       INNER JOIN sys.types AS ty
           ON ty.user_type_id = c.user_type_id
       WHERE s.name = N'dbo'
         AND t.name = N'InternalMessages'
         AND c.name = N'RowVersion'
         AND ty.name <> N'timestamp'
   )
BEGIN
    ALTER TABLE [dbo].[InternalMessages]
        DROP COLUMN [RowVersion];

    ALTER TABLE [dbo].[InternalMessages]
        ADD [RowVersion] rowversion NOT NULL;
END;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
            {
                return;
            }

            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
            {
                return;
            }

            migrationBuilder.Sql(
                @"
IF COL_LENGTH(N'dbo.InternalMessages', N'RowVersion') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[InternalMessages]
        DROP COLUMN [RowVersion];

    ALTER TABLE [dbo].[InternalMessages]
        ADD [RowVersion] varbinary(max) NOT NULL
            CONSTRAINT [DF_InternalMessages_RowVersion_Rollback]
            DEFAULT (0x);
END;
");

            migrationBuilder.Sql(
                @"
IF COL_LENGTH(N'dbo.MessageConversations', N'RowVersion') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[MessageConversations]
        DROP COLUMN [RowVersion];

    ALTER TABLE [dbo].[MessageConversations]
        ADD [RowVersion] varbinary(max) NOT NULL
            CONSTRAINT [DF_MessageConversations_RowVersion_Rollback]
            DEFAULT (0x);
END;
");
        }
    }
}
