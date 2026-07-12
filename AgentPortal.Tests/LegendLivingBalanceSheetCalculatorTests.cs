using System.Text.Json;
using Shared.Finance;
using Xunit;

namespace AgentPortal.Tests;

public class LegendLivingBalanceSheetCalculatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void NormalizeJson_MigratesLegacyIfDieShape_AndCanonicalizesEstatePlan()
    {
        const string legacyJson = """
        {
          "protection": {
            "ifDie": {
              "status": "Partial",
              "coverageAmount": 125000,
              "gapAmount": 75000
            },
            "willsTrusts": {
              "status": "Protected",
              "coverageAmount": 999,
              "gapAmount": 888
            }
          }
        }
        """;

        var normalizedJson = LegendLivingBalanceSheetCalculator.NormalizeJson(legacyJson);
        var state = JsonSerializer.Deserialize<LegendLivingBalanceSheetState>(normalizedJson, JsonOptions);

        Assert.NotNull(state);
        Assert.Equal(LegendProtectionStatuses.Partial, state!.Protection.IfDie.Primary.Status);
        Assert.Equal(125000m, state.Protection.IfDie.Primary.CoverageAmount);
        Assert.Equal(75000m, state.Protection.IfDie.Primary.GapAmount);
        Assert.Equal(LegendProtectionStatuses.Exposed, state.Protection.IfDie.Spouse.Status);
        Assert.Equal("primary", state.Protection.IfDie.ActivePerson);

        Assert.Equal(LegendEstatePlanStatuses.FullEstatePlan, state.Protection.WillsTrusts.Status);
        Assert.Equal(LegendEstateRiskLevels.Low, state.Protection.WillsTrusts.RiskLevel);
    }

    [Fact]
    public void Calculate_UsesCanonicalProtectionSummaryRules()
    {
        var state = LegendLivingBalanceSheetCalculator.Calculate(new LegendLivingBalanceSheetState
        {
            Protection = new LegendBalanceSheetProtection
            {
                IfSued = new LegendDualProtectionItem
                {
                    Primary = new LegendProtectionItem { Status = LegendProtectionStatuses.Protected, CoverageAmount = 100000m, GapAmount = 0m },
                    Spouse = new LegendProtectionItem { Status = LegendProtectionStatuses.Exposed, CoverageAmount = 25000m, GapAmount = 5000m }
                },
                IfSick = new LegendDualProtectionItem
                {
                    Primary = new LegendProtectionItem { Status = LegendProtectionStatuses.Partial, CoverageAmount = 0m, GapAmount = 20000m },
                    Spouse = new LegendProtectionItem { Status = LegendProtectionStatuses.Exposed, CoverageAmount = 0m, GapAmount = 0m }
                },
                IfDie = new LegendDualProtectionItem
                {
                    Primary = new LegendProtectionItem { Status = LegendProtectionStatuses.Exposed, CoverageAmount = 0m, GapAmount = 50000m },
                    Spouse = new LegendProtectionItem { Status = LegendProtectionStatuses.Exposed, CoverageAmount = 10000m, GapAmount = 15000m }
                },
                WillsTrusts = new LegendEstatePlanningItem
                {
                    Status = LegendEstatePlanStatuses.FullEstatePlan,
                    RiskLevel = LegendEstateRiskLevels.Low
                }
            }
        });

        Assert.Equal(135000m, state.Summary.ProtectionCoverageTotal);
        Assert.Equal(90000m, state.Summary.ProtectionGapTotal);
        Assert.Equal(1, state.Summary.ProtectedCount);
        Assert.Equal(1, state.Summary.PartialCount);
        Assert.Equal(1, state.Summary.ExposedCount);
        Assert.Equal(LegendEstatePlanStatuses.FullEstatePlan, state.Summary.EstatePlanningStatus);
        Assert.Equal(LegendEstateRiskLevels.Low, state.Summary.EstatePlanningRiskLevel);
    }
}
