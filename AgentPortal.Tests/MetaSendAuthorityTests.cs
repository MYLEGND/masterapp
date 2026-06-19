using System;
using System.Threading.Tasks;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProtectWebsite.Services.Meta;
using Xunit;

namespace AgentPortal.Tests;

public class MetaSendAuthorityTests
{
    [Fact]
    public async Task TrySendAsync_UsesExplicitDeduplicationKeyBeforeLeadAndSessionIdentity()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var authority = new MetaSendAuthority(db, NullLogger<MetaSendAuthority>.Instance);
        var now = DateTime.UtcNow;
        const string eventKey = "Lead:shared-authority-key";

        var first = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead",
            LeadId = Guid.NewGuid(),
            SessionId = "session-a",
            EventUtc = now,
            DeduplicationKey = eventKey,
            Source = MetaSendAuthoritySources.MetaSignalAnalyticsBridge
        });

        var second = await authority.TrySendAsync(new MetaSendAuthorityRequest
        {
            EventType = "Lead",
            LeadId = Guid.NewGuid(),
            SessionId = "session-b",
            EventUtc = now,
            DeduplicationKey = eventKey,
            Source = MetaSendAuthoritySources.MetaSignalAnalyticsBridge
        });

        Assert.True(first.Allowed);
        Assert.Equal(eventKey, first.DedupeKey);

        Assert.False(second.Allowed);
        Assert.Equal(eventKey, second.DedupeKey);
    }
}
