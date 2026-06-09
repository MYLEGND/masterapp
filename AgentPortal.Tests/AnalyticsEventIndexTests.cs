using System;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsEventIndexTests
{
    [Fact]
    public void AnalyticsEvent_ClientEventId_HasUniqueFilteredIndex()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var entity = db.Model.FindEntityType(typeof(AnalyticsEvent));
        Assert.NotNull(entity);

        var index = Assert.Single(entity!.GetIndexes(), x =>
            x.Properties.Count == 1 &&
            string.Equals(x.Properties[0].Name, nameof(AnalyticsEvent.ClientEventId), StringComparison.Ordinal));

        Assert.True(index.IsUnique);
        Assert.Equal("UX_AnalyticsEvents_ClientEventId", index.GetDatabaseName());
        Assert.Equal("[ClientEventId] IS NOT NULL", index.GetFilter());
    }
}
