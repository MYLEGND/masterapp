using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace AgentPortal.Tests;

// Existing hosted lifecycle fixtures opt in explicitly. They test canonical
// compilation/evaluation mechanics; local shared-weight admission has its own
// rights/privacy cases and never inherits this fixture configuration.
internal static class LegendModelTrainingTestConfiguration
{
    internal static IConfiguration Hosted => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:ModelTraining:Backend"] = "OpenAI"
        }).Build();
}
