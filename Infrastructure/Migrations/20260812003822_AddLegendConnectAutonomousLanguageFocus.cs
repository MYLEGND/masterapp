using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegendConnectAutonomousLanguageFocus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegendConnectAutonomousLanguageFocuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuntimePolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetLanguageCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegendConnectAutonomousLanguageFocuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegendConnectAutonomousLanguageFocuses_LegendConnectRuntimePolicies_RuntimePolicyId",
                        column: x => x.RuntimePolicyId,
                        principalTable: "LegendConnectRuntimePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegendConnectAutonomousLanguageFocuses_RuntimePolicyId_TargetLanguageCode",
                table: "LegendConnectAutonomousLanguageFocuses",
                columns: new[] { "RuntimePolicyId", "TargetLanguageCode" },
                unique: true);

            // Preserve an existing Founder target override as the equivalent
            // focused English expansion. Pair overrides are carried only when
            // they already point from English to a target language; other
            // legacy pair policies retain their existing behavior until a
            // Founder explicitly chooses the new focused acquisition flow.
            if (ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
INSERT INTO [LegendConnectAutonomousLanguageFocuses] ([Id], [RuntimePolicyId], [TargetLanguageCode], [CreatedUtc], [UpdatedUtc])
SELECT NEWID(), [policy].[Id], [focus].[TargetLanguageCode], SYSUTCDATETIME(), SYSUTCDATETIME()
FROM [LegendConnectRuntimePolicies] AS [policy]
CROSS APPLY (VALUES (
    CASE
        WHEN NULLIF(LTRIM(RTRIM([policy].[PriorityLanguageCode])), N'') IS NOT NULL
            THEN LTRIM(RTRIM([policy].[PriorityLanguageCode]))
        WHEN [policy].[PriorityPairKey] LIKE N'en:%' AND LEN([policy].[PriorityPairKey]) > 3
            THEN LTRIM(RTRIM(SUBSTRING([policy].[PriorityPairKey], 4, LEN([policy].[PriorityPairKey]) - 3)))
        ELSE NULL
    END
)) AS [focus]([TargetLanguageCode])
WHERE [policy].[PriorityMode] = N'FounderOverride'
  AND [focus].[TargetLanguageCode] IS NOT NULL
  AND LOWER([focus].[TargetLanguageCode]) <> N'en'
  AND NOT EXISTS (
      SELECT 1
      FROM [LegendConnectAutonomousLanguageFocuses] AS [existing]
      WHERE [existing].[RuntimePolicyId] = [policy].[Id]
        AND [existing].[TargetLanguageCode] = [focus].[TargetLanguageCode]);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegendConnectAutonomousLanguageFocuses");
        }
    }
}
