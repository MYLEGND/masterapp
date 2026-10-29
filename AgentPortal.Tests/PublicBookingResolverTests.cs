using Infrastructure.Bookings;
using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Microsoft.Extensions.Options;
using Moq;
using ProtectWebsite.Services.Booking;
using Xunit;
using System.Linq;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    public async Task ResolveAsync_PrefersConfiguredDuplicateAgentProfile_WhenMatchingTrackingProfileIsStale()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var founderTrackingProfileId = Guid.NewGuid();

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = founderTrackingProfileId,
            AgentUserId = "founder-stale",
            AgentUpn = "founder@example.com",
            Slug = "founder"
        });
        db.AgentProfiles.AddRange(
            new AgentProfile
            {
                AgentUserId = "founder-stale",
                AgentUpn = "founder@example.com",
                NormalizedEmail = "founder@example.com",
                FullName = "Founder Stale"
            },
            new AgentProfile
            {
                AgentUserId = "founder-current",
                AgentUpn = "founder@example.com",
                NormalizedEmail = "founder@example.com",
                FullName = "Founder Current",
                BookingEnabled = true,
                MicrosoftBookingsEmbedUrl = "https://bookings.example.com/founder/embed",
                FallbackBookingUrl = "https://bookings.example.com/founder/fallback",
                CalendarEmail = "calendar-founder@example.com"
            });
        await db.SaveChangesAsync();

        var resolver = new PublicBookingResolver(db, BuildOptions(new PublicBookingOptions
        {
            Enabled = false,
            MicrosoftBookingsEmbedUrl = "",
            FallbackBookingUrl = ""
        }));

        var resolution = await resolver.ResolveAsync(
            new PublicBookingResolveContext(
                AgentTrackingProfileId: founderTrackingProfileId,
                AgentUserId: "founder-stale",
                AgentSlug: "founder"),
            CancellationToken.None);

        Assert.True(resolution.Enabled);
        Assert.Equal(PublicBookingConfigurationSources.AgentProfile, resolution.ConfigurationSource);
        Assert.Equal("https://bookings.example.com/founder/embed", resolution.EmbedUrl);
        Assert.Equal("https://bookings.example.com/founder/fallback", resolution.FallbackUrl);
        Assert.Equal("calendar-founder@example.com", resolution.CalendarEmail);
    }


    private sealed class ProfileMaterializationAudit : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
    {
        public int Profiles { get; private set; }
        public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData data, object entity)
        {
            if (entity is AgentProfile) Profiles++;
            return entity;
        }
    }

    [Fact]
    public async Task BookingProjectionPreservesConfiguredOwnerRankingWithoutMaterializingImageProfiles()
    {
        var audit = new ProfileMaterializationAudit();
        await using var db = ControllerTestHelpers.BuildDb(audit);
        db.AgentTrackingProfiles.Add(new AgentTrackingProfile { AgentUserId = "owner", AgentUpn = "shared@example.test", Slug = "owner" });
        var now = DateTime.UtcNow;
        db.AgentProfiles.AddRange(
            new AgentProfile { AgentUserId = "owner", UpdatedUtc = now, ProfileImageContent = new byte[2_000_000] },
            new AgentProfile { AgentUserId = "other", NormalizedEmail = "shared@example.test", UpdatedUtc = now, BookingEnabled = true, MicrosoftBookingsEmbedUrl = "https://book.example.test/other", ProfileImageContent = new byte[2_000_000] },
            new AgentProfile { AgentUserId = "owner", UpdatedUtc = now.AddDays(-1), BookingEnabled = true, PreferModalOnMobile = true, MicrosoftBookingsEmbedUrl = "https://book.example.test/owner", FallbackBookingUrl = "https://book.example.test/fallback", CalendarUserId = "calendar-owner", CalendarEmail = "OWNER@EXAMPLE.TEST", BookingPageIdOrMailbox = "mailbox", ProfileImageContent = new byte[2_000_000] });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var resolver = new PublicBookingResolver(db, BuildOptions(new PublicBookingOptions()));
        var result = await resolver.ResolveAsync(new PublicBookingResolveContext(AgentUserId: "owner", AgentSlug: "owner"));
        Assert.True(result.Enabled);
        Assert.True(result.PreferModalOnMobile);
        Assert.Equal("https://book.example.test/owner", result.EmbedUrl);
        Assert.Equal("https://book.example.test/fallback", result.FallbackUrl);
        Assert.Equal("calendar-owner", result.CalendarUserId);
        Assert.Equal("owner@example.test", result.CalendarEmail);
        Assert.Equal("mailbox", result.BookingPageIdOrMailbox);
        Assert.Equal("owner", result.AgentUserId);
        Assert.Equal(0, audit.Profiles);
    }

    [Theory]
    [InlineData("booking")]
    [InlineData("phone")]
    public void ProfileProjectionGeneratedSqlExcludesImageAndCredentialPayloads(string kind)
    {
        // Query translation only: no connection, credentials, or provider operation.
        using var db = new Infrastructure.Data.MasterAppDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>()
                .UseSqlServer("Server=localhost;Database=projection_only;Integrated Security=true;TrustServerCertificate=true").Options);
        var owner = kind == "booking" ? typeof(PublicBookingResolver) : typeof(Infrastructure.WebsiteEditing.Controllers.WebsitePlatformController);
        var method = owner.GetMethod(kind == "booking" ? "ProjectBookingProfiles" : "ProjectProtectPhones",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var filtered = db.AgentProfiles.AsNoTracking().Where(p => p.AgentUserId == "owner").OrderByDescending(p => p.UpdatedUtc);
        var projected = Assert.IsAssignableFrom<System.Linq.IQueryable>(method.Invoke(null, new object[] { filtered }));
        var sql = projected.ToQueryString();
        Assert.DoesNotContain("ProfileImage", sql);
        Assert.DoesNotContain("MetaCapiAccessToken", sql);
        Assert.DoesNotContain("ShortBio", sql);
        Assert.Contains("AgentUserId", sql); // owner predicate is preserved
        Assert.Contains("ORDER BY", sql);
        Assert.Contains(kind == "booking" ? "BookingEnabled" : "Phone", sql);
    }

    private static IOptionsSnapshot<PublicBookingOptions> BuildOptions(PublicBookingOptions options)
    {
        var snapshot = new Mock<IOptionsSnapshot<PublicBookingOptions>>();
        snapshot.SetupGet(x => x.Value).Returns(options);
        snapshot.Setup(x => x.Get(It.IsAny<string>())).Returns(options);
        return snapshot.Object;
    }
}
