using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Microsoft.Extensions.Options;
using Moq;
using ProtectWebsite.Services.Booking;
using Xunit;

namespace AgentPortal.Tests;

public class PublicBookingResolverTests
{
    [Fact]
    public async Task ResolveAsync_AgentProfileConfig_WinsOverGlobalFallback()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var trackingProfileId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = trackingProfileId,
            AgentUserId = "agent-1",
            AgentUpn = "alpha@example.com",
            Slug = "alpha"
        });
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-1",
            AgentUpn = "alpha@example.com",
            NormalizedEmail = "alpha@example.com",
            BookingEnabled = true,
            MicrosoftBookingsEmbedUrl = "https://bookings.example.com/alpha/embed",
            FallbackBookingUrl = "https://bookings.example.com/alpha/fallback",
            CalendarEmail = "calendar-alpha@example.com",
            BookingPageIdOrMailbox = "alpha-mailbox"
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            Id = 1,
            LeadId = websiteLeadId,
            FirstName = "Jordan",
            LastName = "Miles",
            Email = "jordan@example.com",
            AgentTrackingProfileId = trackingProfileId,
            AgentSlug = "alpha",
            CreatedUtc = DateTime.UtcNow,
            Status = "New"
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadRowId = 1,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "lead-1",
            AgentUserId = "agent-1",
            Bucket = "LifeInsurance",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PublicBookingResolver(db, BuildOptions(new PublicBookingOptions
        {
            Enabled = true,
            MicrosoftBookingsEmbedUrl = "https://bookings.example.com/global/embed",
            FallbackBookingUrl = "https://bookings.example.com/global/fallback"
        }));

        var resolution = await resolver.ResolveAsync(
            new PublicBookingResolveContext(WebsiteLeadId: websiteLeadId, AgentSlug: "beta"),
            CancellationToken.None);

        Assert.True(resolution.Enabled);
        Assert.Equal("https://bookings.example.com/alpha/embed", resolution.EmbedUrl);
        Assert.Equal("https://bookings.example.com/alpha/fallback", resolution.FallbackUrl);
        Assert.Equal(PublicBookingConfigurationSources.AgentProfile, resolution.ConfigurationSource);
        Assert.Equal("alpha", resolution.AgentSlug);
        Assert.Equal("agent-1", resolution.AgentUserId);
        Assert.Equal("calendar-alpha@example.com", resolution.CalendarEmail);
        Assert.Equal("alpha-mailbox", resolution.BookingPageIdOrMailbox);
    }

    [Fact]
    public async Task ResolveAsync_SlugOverride_WinsWhenAgentProfileHasNoBookingConfig()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-1",
            AgentUpn = "alpha@example.com",
            Slug = "alpha"
        });
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-1",
            AgentUpn = "alpha@example.com",
            NormalizedEmail = "alpha@example.com"
        });
        await db.SaveChangesAsync();

        var resolver = new PublicBookingResolver(db, BuildOptions(new PublicBookingOptions
        {
            Enabled = true,
            MicrosoftBookingsEmbedUrl = "https://bookings.example.com/global/embed",
            FallbackBookingUrl = "https://bookings.example.com/global/fallback",
            AgentOverrides =
            {
                ["alpha"] = new PublicBookingAgentOverride
                {
                    Enabled = true,
                    MicrosoftBookingsEmbedUrl = "https://bookings.example.com/alpha-override/embed",
                    FallbackBookingUrl = "https://bookings.example.com/alpha-override/fallback",
                    CalendarEmail = "override-alpha@example.com"
                }
            }
        }));

        var resolution = await resolver.ResolveAsync(
            new PublicBookingResolveContext(AgentSlug: "alpha"),
            CancellationToken.None);

        Assert.True(resolution.Enabled);
        Assert.Equal(PublicBookingConfigurationSources.SlugOverride, resolution.ConfigurationSource);
        Assert.Equal("https://bookings.example.com/alpha-override/embed", resolution.EmbedUrl);
        Assert.Equal("override-alpha@example.com", resolution.CalendarEmail);
    }

    [Fact]
    public async Task ResolveAsync_GlobalFallback_IsUsedOnlyWhenAgentConfigIsMissing()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "agent-1",
            AgentUpn = "alpha@example.com",
            Slug = "alpha"
        });
        await db.SaveChangesAsync();

        var resolver = new PublicBookingResolver(db, BuildOptions(new PublicBookingOptions
        {
            Enabled = true,
            MicrosoftBookingsEmbedUrl = "https://bookings.example.com/global/embed",
            FallbackBookingUrl = "https://bookings.example.com/global/fallback",
            CalendarEmail = "global@example.com"
        }));

        var resolution = await resolver.ResolveAsync(
            new PublicBookingResolveContext(AgentSlug: "alpha"),
            CancellationToken.None);

        Assert.True(resolution.Enabled);
        Assert.Equal(PublicBookingConfigurationSources.GlobalFallback, resolution.ConfigurationSource);
        Assert.Equal("https://bookings.example.com/global/embed", resolution.EmbedUrl);
        Assert.Equal("global@example.com", resolution.CalendarEmail);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotLeakAnotherAgentsBookingUrlIntoResolvedLeadFlow()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var alphaTrackingProfileId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();

        db.AgentTrackingProfiles.AddRange(
            new AgentTrackingProfile
            {
                Id = alphaTrackingProfileId,
                AgentUserId = "agent-1",
                AgentUpn = "alpha@example.com",
                Slug = "alpha"
            },
            new AgentTrackingProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = "agent-2",
                AgentUpn = "beta@example.com",
                Slug = "beta"
            });
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-1",
            AgentUpn = "alpha@example.com",
            NormalizedEmail = "alpha@example.com",
            BookingEnabled = true,
            MicrosoftBookingsEmbedUrl = "https://bookings.example.com/alpha/embed",
            FallbackBookingUrl = "https://bookings.example.com/alpha/fallback"
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            Id = 1,
            LeadId = websiteLeadId,
            FirstName = "Jordan",
            LastName = "Miles",
            Email = "jordan@example.com",
            AgentTrackingProfileId = alphaTrackingProfileId,
            AgentSlug = "alpha",
            CreatedUtc = DateTime.UtcNow,
            Status = "New"
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = Guid.NewGuid(),
            WebsiteLeadRowId = 1,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "lead-1",
            AgentUserId = "agent-1",
            Bucket = "LifeInsurance",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PublicBookingResolver(db, BuildOptions(new PublicBookingOptions
        {
            Enabled = true,
            MicrosoftBookingsEmbedUrl = "https://bookings.example.com/global/embed",
            FallbackBookingUrl = "https://bookings.example.com/global/fallback",
            AgentOverrides =
            {
                ["beta"] = new PublicBookingAgentOverride
                {
                    Enabled = true,
                    MicrosoftBookingsEmbedUrl = "https://bookings.example.com/beta/embed",
                    FallbackBookingUrl = "https://bookings.example.com/beta/fallback"
                }
            }
        }));

        var resolution = await resolver.ResolveAsync(
            new PublicBookingResolveContext(WebsiteLeadId: websiteLeadId, AgentSlug: "beta"),
            CancellationToken.None);

        Assert.Equal(PublicBookingConfigurationSources.AgentProfile, resolution.ConfigurationSource);
        Assert.Equal("alpha", resolution.AgentSlug);
        Assert.Equal("https://bookings.example.com/alpha/embed", resolution.EmbedUrl);
        Assert.NotEqual("https://bookings.example.com/beta/embed", resolution.EmbedUrl);
    }

    private static IOptionsSnapshot<PublicBookingOptions> BuildOptions(PublicBookingOptions options)
    {
        var snapshot = new Mock<IOptionsSnapshot<PublicBookingOptions>>();
        snapshot.SetupGet(x => x.Value).Returns(options);
        snapshot.Setup(x => x.Get(It.IsAny<string>())).Returns(options);
        return snapshot.Object;
    }
}
