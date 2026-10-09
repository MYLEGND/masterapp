using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Model-neutral raw control-plane schema. These tables/columns are intentionally
    /// not EF-mapped; the canonical engineering authorities access them through
    /// serialized, bounded SQL commands. The matching generated designer therefore
    /// carries the unchanged MasterAppDbContext target model.
    /// </summary>
    public partial class ExtendFounderEngineeringActivation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE [LegendEngineeringOperationalContract]
                  ADD [AutonomousEngineeringEnabled] bit NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContract_AutonomousEngineeringEnabled]
                      DEFAULT CAST(1 AS bit),
                      [HeadGptModel] nvarchar(160) NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContract_HeadGptModel]
                      DEFAULT N'auto',
                      [CodexModel] nvarchar(160) NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContract_CodexModel]
                      DEFAULT N'auto',
                      [ReviewerModel] nvarchar(160) NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContract_ReviewerModel]
                      DEFAULT N'auto';

                ALTER TABLE [LegendEngineeringOperationalContractHistory]
                  ADD [AutonomousEngineeringEnabled] bit NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContractHistory_AutonomousEngineeringEnabled]
                      DEFAULT CAST(1 AS bit),
                      [HeadGptModel] nvarchar(160) NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContractHistory_HeadGptModel]
                      DEFAULT N'auto',
                      [CodexModel] nvarchar(160) NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContractHistory_CodexModel]
                      DEFAULT N'auto',
                      [ReviewerModel] nvarchar(160) NOT NULL
                      CONSTRAINT [DF_LegendEngineeringOperationalContractHistory_ReviewerModel]
                      DEFAULT N'auto';

                CREATE TABLE [LegendEngineeringChatGptPlanClientRegistration] (
                    [RegistrationKey] nvarchar(64) NOT NULL,
                    [ClientId] nvarchar(200) NOT NULL,
                    [AuthenticationMethod] nvarchar(32) NOT NULL,
                    [ClientSecretCiphertext] nvarchar(max) NULL,
                    [EligibilityConfirmed] bit NOT NULL,
                    [Revision] nvarchar(32) NOT NULL,
                    [UpdatedUtc] datetime2 NOT NULL,
                    CONSTRAINT [PK_LegendEngineeringChatGptPlanClientRegistration]
                        PRIMARY KEY ([RegistrationKey])
                );

                CREATE TABLE [LegendEngineeringChatGptPlanOAuthTransactions] (
                    [StateHash] nvarchar(64) NOT NULL,
                    [ClientId] nvarchar(200) NOT NULL,
                    [CodeVerifierCiphertext] nvarchar(max) NOT NULL,
                    [NonceCiphertext] nvarchar(max) NOT NULL,
                    [RedirectUri] nvarchar(512) NOT NULL,
                    [CreatedUtc] datetime2 NOT NULL,
                    [ExpiresUtc] datetime2 NOT NULL,
                    CONSTRAINT [PK_LegendEngineeringChatGptPlanOAuthTransactions]
                        PRIMARY KEY ([StateHash])
                );

                CREATE INDEX [IX_LegendEngineeringChatGptPlanOAuthTransactions_ExpiresUtc]
                    ON [LegendEngineeringChatGptPlanOAuthTransactions] ([ExpiresUtc]);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE [LegendEngineeringChatGptPlanOAuthTransactions];
                DROP TABLE [LegendEngineeringChatGptPlanClientRegistration];

                ALTER TABLE [LegendEngineeringOperationalContract]
                    DROP CONSTRAINT [DF_LegendEngineeringOperationalContract_AutonomousEngineeringEnabled],
                         [DF_LegendEngineeringOperationalContract_HeadGptModel],
                         [DF_LegendEngineeringOperationalContract_CodexModel],
                         [DF_LegendEngineeringOperationalContract_ReviewerModel];

                ALTER TABLE [LegendEngineeringOperationalContract]
                    DROP COLUMN [AutonomousEngineeringEnabled],
                                [HeadGptModel],
                                [CodexModel],
                                [ReviewerModel];

                ALTER TABLE [LegendEngineeringOperationalContractHistory]
                    DROP CONSTRAINT [DF_LegendEngineeringOperationalContractHistory_AutonomousEngineeringEnabled],
                         [DF_LegendEngineeringOperationalContractHistory_HeadGptModel],
                         [DF_LegendEngineeringOperationalContractHistory_CodexModel],
                         [DF_LegendEngineeringOperationalContractHistory_ReviewerModel];

                ALTER TABLE [LegendEngineeringOperationalContractHistory]
                    DROP COLUMN [AutonomousEngineeringEnabled],
                                [HeadGptModel],
                                [CodexModel],
                                [ReviewerModel];
                """);
        }
    }
}
