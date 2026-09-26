using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalAppointmentOutcomeTests
{
    [Theory]
    [InlineData(LeadAppointmentStatus.Booked, AppointmentAnalyticsEventCatalog.Booked, true)]
    [InlineData(LeadAppointmentStatus.Confirmed, AppointmentAnalyticsEventCatalog.Booked, true)]
    [InlineData(LeadAppointmentStatus.Rescheduled, AppointmentAnalyticsEventCatalog.Rescheduled, false)]
    [InlineData(LeadAppointmentStatus.Cancelled, AppointmentAnalyticsEventCatalog.Cancelled, false)]
    [InlineData(LeadAppointmentStatus.Completed, AppointmentAnalyticsEventCatalog.Completed, true)]
    [InlineData(LeadAppointmentStatus.NoShow, AppointmentAnalyticsEventCatalog.NoShow, false)]
    public async Task AppointmentLifecycleUsesOneScopedCanonicalAnalyticsWriter(
        LeadAppointmentStatus status,
        string expectedEvent,
        bool expectedMetaEligibility)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var websiteLeadId = Guid.NewGuid();
        var intakeId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();

        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            CommerceBusinessId = businessId,
            FirstName = "Jordan",
            Email = "jordan@example.test",
            Phone = "6025550100",
            SourcePageKey = "/contact",
            SessionId = "session-1",
            VisitorId = "visitor-1",
            UtmCampaign = "campaign-1",
            CreatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = intakeId,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "business-contact",
            CommerceBusinessId = businessId,
            SessionId = "session-1",
            VisitorId = "visitor-1",
            SourcePageKey = "/contact",
            UtmCampaign = "campaign-1",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var appointment = new LeadAppointment
        {
            Id = appointmentId,
            WorkstationLeadId = "business-contact",
            WebsiteLeadIntakeLinkId = intakeId,
            BookingSource = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
            ConfirmationSource = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
            CreatedUtc = DateTime.UtcNow
        };
        appointment.ApplyStatus(status, DateTime.UtcNow);

        var service = new MetaSignalCrmOutcomeService(
            db,
            NullLogger<MetaSignalCrmOutcomeService>.Instance);

        await service.RecordAppointmentOutcomeAsync(appointment);
        await db.SaveChangesAsync();

        // Same status replay must not duplicate.
        await service.RecordAppointmentOutcomeAsync(appointment);
        await db.SaveChangesAsync();

        var row = Assert.Single(await db.AnalyticsEvents.ToListAsync());
        Assert.Equal(expectedEvent, row.EventType);
        Assert.Equal(businessId, row.CommerceBusinessId);
        Assert.Null(row.AgentTrackingProfileId);
        Assert.Equal("session-1", row.SessionId);
        Assert.Equal("visitor-1", row.VisitorId);
        Assert.Equal("campaign-1", row.UtmCampaign);
        Assert.True(MetaSignalSingleTruthPolicy.ReadBoolean(row.MetadataJson, "isServerAuthority"));
        Assert.Equal(expectedMetaEligibility,
            MetaSignalSingleTruthPolicy.ReadBoolean(row.MetadataJson, "metaServerAuthorityEligible"));
    }

    [Fact]
    public async Task DistinctAppointmentStatusesRemainDistinctEventsForOneAppointment()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var websiteLeadId = Guid.NewGuid();
        var intakeId = Guid.NewGuid();
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = websiteLeadId,
            AgentTrackingProfileId = Guid.NewGuid(),
            AgentSlug = "agent-one",
            FirstName = "Jordan",
            Email = "jordan@example.test",
            CreatedUtc = DateTime.UtcNow
        });
        db.WebsiteLeadIntakeLinks.Add(new WebsiteLeadIntakeLink
        {
            Id = intakeId,
            WebsiteLeadPublicId = websiteLeadId,
            WorkstationLeadId = "lead-1",
            AgentUserId = "agent-one-user",
            SubmittedUtc = DateTime.UtcNow,
            CapturedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var appointment = new LeadAppointment
        {
            Id = Guid.NewGuid(),
            WorkstationLeadId = "lead-1",
            WebsiteLeadIntakeLinkId = intakeId,
            BookingSource = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
            ConfirmationSource = LeadAppointmentBookingSources.MicrosoftGraphWebhook,
            CreatedUtc = DateTime.UtcNow
        };
        var service = new MetaSignalCrmOutcomeService(
            db,
            NullLogger<MetaSignalCrmOutcomeService>.Instance);

        appointment.ApplyStatus(LeadAppointmentStatus.Booked, DateTime.UtcNow);
        await service.RecordAppointmentOutcomeAsync(appointment);
        await db.SaveChangesAsync();

        appointment.ApplyStatus(LeadAppointmentStatus.Rescheduled, DateTime.UtcNow.AddMinutes(1));
        await service.RecordAppointmentOutcomeAsync(appointment);
        await db.SaveChangesAsync();

        appointment.ApplyStatus(LeadAppointmentStatus.Cancelled, DateTime.UtcNow.AddMinutes(2));
        await service.RecordAppointmentOutcomeAsync(appointment);
        await db.SaveChangesAsync();

        var eventTypes = await db.AnalyticsEvents.OrderBy(x => x.EventUtc).Select(x => x.EventType).ToListAsync();
        Assert.Equal(new[]
        {
            AppointmentAnalyticsEventCatalog.Booked,
            AppointmentAnalyticsEventCatalog.Rescheduled,
            AppointmentAnalyticsEventCatalog.Cancelled
        }, eventTypes);
    }
}
