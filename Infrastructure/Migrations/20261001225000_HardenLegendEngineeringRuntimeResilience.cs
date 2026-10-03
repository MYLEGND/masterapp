using Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Extends the existing engineering work/credential authorities with durable
    /// provider resilience evidence. No parallel queue, scheduler, credential store,
    /// inference backend, or release authority is introduced.
    /// </summary>
    public partial class HardenLegendEngineeringRuntimeResilience : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE [LegendEngineeringChatGptPlanCredentials] ADD
                    [ProviderBlockerClass] nvarchar(48) NULL,
                    [ProviderBlockerCode] nvarchar(128) NULL,
                    [ProviderBlockedUtc] datetime2 NULL,
                    [ProviderRetryNotBeforeUtc] datetime2 NULL,
                    [ProviderRequestId] nvarchar(160) NULL,
                    [ProviderHttpStatus] int NULL,
                    [ProviderErrorShape] nvarchar(48) NULL,
                    [ProviderErrorParam] nvarchar(160) NULL,
                    [ProviderCircuitEpisodeId] nvarchar(32) NULL,
                    [ProviderRecoveredEpisodeId] nvarchar(32) NULL,
                    [ProviderRecoveredUtc] datetime2 NULL,
                    [ProviderFailureStreak] int NOT NULL
                        CONSTRAINT [DF_LegendEngineeringChatGptPlanCredentials_ProviderFailureStreak] DEFAULT 0,
                    [ReadinessState] nvarchar(48) NOT NULL
                        CONSTRAINT [DF_LegendEngineeringChatGptPlanCredentials_ReadinessState] DEFAULT N'UNVERIFIED',
                    [ReadinessSignature] nvarchar(64) NULL,
                    [ReadinessModelsJson] nvarchar(2000) NULL,
                    [ReadinessCheckedUtc] datetime2 NULL,
                    [ReadinessResponseId] nvarchar(160) NULL,
                    [ReadinessRequestId] nvarchar(160) NULL,
                    [ReadinessCode] nvarchar(128) NULL,
                    [ProviderExecutionLeaseIdentity] nvarchar(32) NULL,
                    [ProviderExecutionLeaseOwner] nvarchar(128) NULL,
                    [ProviderExecutionLeaseUntilUtc] datetime2 NULL;

                ALTER TABLE [LegendEngineeringUsage] ADD
                    [ProviderAttempted] bit NOT NULL
                        CONSTRAINT [DF_LegendEngineeringUsage_ProviderAttempted] DEFAULT CAST(0 AS bit),
                    [LogicalAttemptCompleted] bit NOT NULL
                        CONSTRAINT [DF_LegendEngineeringUsage_LogicalAttemptCompleted] DEFAULT CAST(0 AS bit),
                    [ProviderOutcome] nvarchar(48) NULL,
                    [ProviderStatusCode] int NULL,
                    [ProviderErrorShape] nvarchar(48) NULL,
                    [ProviderErrorCode] nvarchar(128) NULL,
                    [ProviderErrorParam] nvarchar(160) NULL,
                    [ProviderRequestId] nvarchar(160) NULL;
                """);

            // SQL Server compiles a raw SQL batch before executing it. Keep the
            // legacy backfill in a separate batch so the newly added columns are
            // resolvable when the UPDATE is compiled.
            migrationBuilder.Sql(
                """
                UPDATE [LegendEngineeringUsage]
                SET [ProviderAttempted]=CAST(1 AS bit),
                    [LogicalAttemptCompleted]=CASE WHEN [UsageObserved]=CAST(1 AS bit) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
                    [ProviderOutcome]=CASE WHEN [UsageObserved]=CAST(1 AS bit) THEN N'COMPLETED' ELSE N'LEGACY_UNKNOWN' END;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE [LegendEngineeringUsage]
                    DROP CONSTRAINT [DF_LegendEngineeringUsage_ProviderAttempted],
                         [DF_LegendEngineeringUsage_LogicalAttemptCompleted];
                ALTER TABLE [LegendEngineeringUsage] DROP COLUMN
                    [ProviderAttempted],[LogicalAttemptCompleted],[ProviderOutcome],
                    [ProviderStatusCode],[ProviderErrorShape],[ProviderErrorCode],[ProviderErrorParam],[ProviderRequestId];

                ALTER TABLE [LegendEngineeringChatGptPlanCredentials]
                    DROP CONSTRAINT [DF_LegendEngineeringChatGptPlanCredentials_ProviderFailureStreak],
                         [DF_LegendEngineeringChatGptPlanCredentials_ReadinessState];
                ALTER TABLE [LegendEngineeringChatGptPlanCredentials] DROP COLUMN
                    [ProviderBlockerClass],[ProviderBlockerCode],[ProviderBlockedUtc],
                    [ProviderRetryNotBeforeUtc],[ProviderRequestId],[ProviderHttpStatus],
                    [ProviderErrorShape],[ProviderErrorParam],[ProviderCircuitEpisodeId],[ProviderRecoveredEpisodeId],
                    [ProviderRecoveredUtc],[ProviderFailureStreak],[ReadinessState],
                    [ReadinessSignature],[ReadinessModelsJson],[ReadinessCheckedUtc],
                    [ReadinessResponseId],[ReadinessRequestId],[ReadinessCode],
                    [ProviderExecutionLeaseIdentity],[ProviderExecutionLeaseOwner],
                    [ProviderExecutionLeaseUntilUtc];
                """);
        }
    }
}
