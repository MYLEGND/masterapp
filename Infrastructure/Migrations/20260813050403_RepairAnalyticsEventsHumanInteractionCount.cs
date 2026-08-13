using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepairAnalyticsEventsHumanInteractionCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
            {
                return;
            }

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[dbo].[AnalyticsEvents]', N'U') IS NULL
                    THROW 51000, 'Canonical table dbo.AnalyticsEvents is required before repairing HumanInteractionCount.', 1;

                IF COL_LENGTH(N'[dbo].[AnalyticsEvents]', N'HumanInteractionCount') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[AnalyticsEvents]
                        ADD [HumanInteractionCount] int NULL;
                END
                ELSE IF EXISTS
                (
                    SELECT 1
                    FROM sys.columns AS c
                    INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                    WHERE c.object_id = OBJECT_ID(N'[dbo].[AnalyticsEvents]')
                      AND c.name = N'HumanInteractionCount'
                      AND (t.name <> N'int' OR c.is_nullable <> 1)
                )
                    THROW 51001, 'dbo.AnalyticsEvents.HumanInteractionCount must be nullable int.', 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This is a forward-only schema-drift repair. Removing a column that may
            // already contain live analytics data would not restore a safe prior state.
        }
    }
}
