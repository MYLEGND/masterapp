using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _20260217_ManualAddRecurringType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return;

            // Add Type column if missing
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.RecurringExpenses', 'Type') IS NULL
BEGIN
    ALTER TABLE [dbo].[RecurringExpenses] ADD [Type] int NOT NULL CONSTRAINT [DF_RecurringExpenses_Type] DEFAULT (0);
END
");

            // Add index if missing
            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_RecurringExpenses_Type'
      AND object_id = OBJECT_ID('dbo.RecurringExpenses')
)
BEGIN
    CREATE INDEX [IX_RecurringExpenses_Type] ON [dbo].[RecurringExpenses]([Type]);
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
                return;

            // Drop index if exists
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_RecurringExpenses_Type'
      AND object_id = OBJECT_ID('dbo.RecurringExpenses')
)
BEGIN
    DROP INDEX [IX_RecurringExpenses_Type] ON [dbo].[RecurringExpenses];
END
");

            // Drop default constraint if it exists, then drop column if exists
            migrationBuilder.Sql(@"
IF COL_LENGTH('dbo.RecurringExpenses', 'Type') IS NOT NULL
BEGIN
    DECLARE @dfName nvarchar(128);

    SELECT @dfName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.default_object_id = dc.object_id
    INNER JOIN sys.tables t
        ON t.object_id = c.object_id
    WHERE t.name = 'RecurringExpenses'
      AND c.name = 'Type';

    IF @dfName IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE [dbo].[RecurringExpenses] DROP CONSTRAINT [' + @dfName + ']');
    END

    ALTER TABLE [dbo].[RecurringExpenses] DROP COLUMN [Type];
END
");
        }
    }
}
