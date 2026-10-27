using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Bookings;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Solutions.BookingBusinesses.Item.GetStaffAvailability;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using Microsoft.Kiota.Serialization.Json;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistCalendarGatewayTests
{
    [Fact]
    public async Task NoticeFilterRunsBeforeThreeSlotLimit()
    {
        using var f = await CalendarFixture.CreateAsync();
        var cutoff = DateTime.SpecifyKind(f.Date.ToDateTime(new TimeOnly(11, 30)), DateTimeKind.Utc);
        var configuration = new ReceptionistBusinessConfiguration
        {
            TimeZoneId = "UTC",
            Hours = Enum.GetValues<DayOfWeek>().Select(day => new ReceptionistHoursWindow(day, new TimeOnly(6, 0), new TimeOnly(21, 0))).ToArray(),
            BookingPolicy = new() { MinimumNoticeBusinessDays = 1 }
        };
        var slots = await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default,
            start => ReceptionistBookingAuthority.NoticeAllows(start, cutoff.AddDays(-1), configuration));
        Assert.NotEmpty(slots);
        Assert.Equal(cutoff, slots[0].StartUtc);
        Assert.All(slots, slot => Assert.True(slot.StartUtc >= cutoff));
        Assert.InRange(slots.Count, 1, 3);
    }

    [Fact]
    public async Task CanonicalEngineUsesExactServiceFullBusyBuffersAndConfirmedRecipientWithOneNonRetriedPost()
    {
        using var f = await CalendarFixture.CreateAsync();
        f.ServicesOnSecondPage = true;
        f.Busy = [f.Appointment(f.Date, "09:30", "10:30", "other-service")];
        var slots = await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default);
        Assert.NotEmpty(slots); Assert.Equal(11, slots[0].StartUtc.Hour); // other-service post-buffer ends at 11.
        Assert.All(slots, slot => Assert.Equal("approved-staff", slot.StaffId));
        Assert.Contains(f.ReadRequests, uri => uri.Contains("/calendarView") && uri.Contains("start=") && uri.Contains("end="));
        Assert.DoesNotContain(f.ReadRequests, uri => uri.Contains("/appointments?") && uri.Contains("filter", StringComparison.OrdinalIgnoreCase));
        var fence = new Fence();
        var command = new ReceptionistTrustedBookingCommit(f.Target, slots[0], true);
        await f.Controller().CreateReceptionistAppointmentAsync(command, fence, default);
        Assert.Equal(1, f.Posts); Assert.Equal(1, fence.Starts); Assert.Equal(1, fence.Identities);
        Assert.True(f.NoPostRetry); Assert.NotNull(f.Sent);
        Assert.Equal(f.Target.InvitationEmail, f.Sent!.Value.GetProperty("customerEmailAddress").GetString());
        Assert.Equal(f.Target.CustomerName, f.Sent.Value.GetProperty("customerName").GetString());
        Assert.False(f.Sent.Value.GetProperty("optOutOfCustomerEmail").GetBoolean());
        Assert.False(f.Sent.Value.GetProperty("smsNotificationsEnabled").GetBoolean());
        Assert.DoesNotContain("5550129999", f.Sent.Value.GetRawText()); // existing profile phone is not sent
        await using var read = new MasterAppDbContext(f.Options);
        var saved = Assert.Single(await read.LeadAppointments.AsNoTracking().ToListAsync());
        Assert.Equal(f.Target.IntakeLinkId, saved.WebsiteLeadIntakeLinkId); Assert.Equal("original-event", saved.CalendarEventId);
        Assert.True(await f.Controller().VerifyReceptionistAppointmentAsync(command, new("original-event", f.Target.CalendarTargetSha256), default));
        var original = await f.Controller().ReadOriginalReceptionistSlotAsync(f.Target, "original-event", default);
        Assert.Equal(slots[0], original);
        var originalRead = Uri.UnescapeDataString(f.ReadRequests.Last());
        Assert.Contains("/appointments/original-event", originalRead); Assert.Contains("select=", originalRead);
        Assert.DoesNotContain("customer", originalRead, StringComparison.OrdinalIgnoreCase);
        f.ProviderRecord!.CustomerEmailAddress = "changed-recipient@example.invalid";
        Assert.False(await f.Controller().VerifyReceptionistAppointmentAsync(command, new("original-event", f.Target.CalendarTargetSha256), default));
    }

    [Fact]
    public async Task OvernightAppointmentsAndAdjacentBuffersRemainBusyForTheChosenStaff()
    {
        using var f = await CalendarFixture.CreateAsync();
        var prior = f.Date.AddDays(-1);
        f.Busy = [new() { Id = "overnight", ServiceId = "other-service", StaffMemberIds = ["approved-staff"],
            StartDateTime = f.Wall(prior, "23:00"), EndDateTime = f.Wall(f.Date, "10:30") }];
        var slot = (await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default))[0];
        Assert.Equal(11, slot.StartUtc.Hour);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteInitialOrSubsequentCalendarPageDoesNotBecomeFreeAvailability(bool initial)
    {
        using var f = await CalendarFixture.CreateAsync(); f.MissingCalendarPage = true; f.MissingFirstPage = initial;
        Assert.Empty(await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default));
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task ExplicitInvitationConsentAndFinalAvailabilityFencePrecedeAnyProviderPost()
    {
        using var f = await CalendarFixture.CreateAsync();
        var slot = (await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default))[0];
        var fence = new Fence();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Controller().CreateReceptionistAppointmentAsync(new(f.Target, slot, false), fence, default));
        Assert.Equal(0, f.Posts); Assert.Equal(0, fence.Starts);
        f.Busy = [f.Appointment(f.Date, "10:00", "12:00", "approved-service")];
        await f.Controller().CreateReceptionistAppointmentAsync(new(f.Target, slot, true), fence, default);
        Assert.Equal(0, f.Posts); Assert.Equal(0, fence.Starts);
    }

    [Fact]
    public async Task AChangedBufferPolicyIsAppliedDuringTheFinalCheck()
    {
        using var f = await CalendarFixture.CreateAsync();
        var slot = (await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default))[0];
        f.FreshPreBuffer = TimeSpan.FromMinutes(30); // direct service GET still has the earlier zero buffer
        var fence = new Fence();
        await f.Controller().CreateReceptionistAppointmentAsync(new(f.Target, slot, true), fence, default);
        Assert.Equal(0, f.Posts); Assert.Equal(0, fence.Starts);
    }

    [Fact]
    public async Task LostProviderIdentityAcknowledgementPreventsLocalWriteAndCannotClaimBooked()
    {
        using var f = await CalendarFixture.CreateAsync();
        var slot = (await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default))[0];
        var fence = new Fence { FailAfterIdentity = true };
        var command = new ReceptionistTrustedBookingCommit(f.Target, slot, true);
        await f.Controller().CreateReceptionistAppointmentAsync(command, fence, default);
        Assert.Equal(1, f.Posts); Assert.Equal(1, fence.Identities);
        Assert.False(await f.Controller().VerifyReceptionistAppointmentAsync(command, new("original-event", f.Target.CalendarTargetSha256), default));
        await using var read = new MasterAppDbContext(f.Options); Assert.Empty(await read.LeadAppointments.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderSuccessAndLocalCommitFailureAreDistinguishedByFreshCanonicalReadback(bool lostCommitAcknowledgement)
    {
        using var f = await CalendarFixture.CreateAsync();
        var slot = (await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default))[0];
        f.LocalFailure.Mode = lostCommitAcknowledgement ? "after" : "before";
        var fence = new Fence(); var command = new ReceptionistTrustedBookingCommit(f.Target, slot, true);
        await f.Controller().CreateReceptionistAppointmentAsync(command, fence, default);
        Assert.Equal(1, f.Posts); Assert.Equal(1, fence.Identities); Assert.Equal(1, f.LocalFailure.Failures);
        Assert.Equal(lostCommitAcknowledgement,
            await f.Controller().VerifyReceptionistAppointmentAsync(command, new("original-event", f.Target.CalendarTargetSha256), default));
        await using var read = new MasterAppDbContext(f.Options);
        Assert.Equal(lostCommitAcknowledgement ? 1 : 0, await read.LeadAppointments.CountAsync());
    }

    [Fact]
    public async Task DistinctActorsSharingACalendarCannotDoubleBookWhenTheProviderViewLags()
    {
        using var f = await CalendarFixture.CreateAsync();
        var second = await f.AddSecondActorAsync();
        var firstSlot = (await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default))[0];
        var secondSlot = (await f.Controller().ReadReceptionistSlotsAsync(second, f.Date, 0, 30, default))[0];
        Assert.Equal(firstSlot.StartUtc, secondSlot.StartUtc);
        f.PostEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.ReleasePost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFence = new Fence(); var secondFence = new Fence();
        var first = f.Controller().CreateReceptionistAppointmentAsync(new(f.Target, firstSlot, true), firstFence, default);
        await f.PostEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var competing = f.Controller().CreateReceptionistAppointmentAsync(new(second, secondSlot, true), secondFence, default);
        Assert.False(competing.IsCompleted);
        f.ReleasePost.SetResult();
        await Task.WhenAll(first, competing).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Busy); // the provider continues to omit the first appointment
        Assert.Equal(1, f.Posts);
        Assert.Equal(1, firstFence.Starts + secondFence.Starts);
        Assert.Equal(1, firstFence.Identities + secondFence.Identities);
        Assert.Equal(0, secondFence.Identities);
        await using var read = new MasterAppDbContext(f.Options);
        var saved = Assert.Single(await read.LeadAppointments.AsNoTracking().ToListAsync());
        Assert.Equal("synthetic-mailbox", saved.BookingPageIdOrMailbox);
        Assert.Equal(f.Target.IntakeLinkId, saved.WebsiteLeadIntakeLinkId);
        Assert.False(await f.Controller().VerifyReceptionistAppointmentAsync(new(second, secondSlot, true),
            new("original-event", second.CalendarTargetSha256), default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed_time")]
    [InlineData("duplicate")]
    [InlineData("unknown_service")]
    [InlineData("prior_day_buffer")]
    [InlineData("reconciled")]
    public async Task SharedCanonicalBookingsNeedMatchingProviderTimesAndBuffersBeforeOfferingAvailability(string state)
    {
        using var f = await CalendarFixture.CreateAsync();
        var day = state == "prior_day_buffer" ? f.Date.AddDays(-1) : f.Date;
        var start = day.ToDateTime(new TimeOnly(state == "prior_day_buffer" ? 23 : 10, 30), DateTimeKind.Utc);
        var end = start.AddMinutes(state == "prior_day_buffer" ? 20 : 30);
        await using (var db = new MasterAppDbContext(f.Options))
        {
            db.LeadAppointments.Add(new() { Id = Guid.NewGuid(), OwnerAgentUserId = "different-canonical-agent",
                BookingPageIdOrMailbox = "synthetic-mailbox", BookingProvider = "microsoft_bookings", CalendarEventId = "saved-event",
                ScheduledStartUtc = start, ScheduledEndUtc = end, Status = LeadAppointmentStatus.Booked });
            await db.SaveChangesAsync();
        }
        if (state is not ("missing" or "prior_day_buffer"))
        {
            var observed = f.Appointment(f.Date, state == "changed_time" ? "10:00" : "10:30",
                state == "changed_time" ? "10:30" : "11:00", state == "unknown_service" ? "missing-service" : "other-service");
            observed.Id = "saved-event"; f.Busy = state == "duplicate" ? [observed, observed] : [observed];
        }
        var slots = await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default);
        if (state == "reconciled")
        {
            Assert.NotEmpty(slots); Assert.DoesNotContain(slots, x => x.StartUtc < end.AddMinutes(30) && x.EndUtc > start);
        }
        else Assert.Empty(slots);
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FractionalProviderPolicyCannotBeRoundedIntoAnApprovedVoiceSlot(bool buffer)
    {
        using var f = await CalendarFixture.CreateAsync();
        if (buffer) f.FreshPreBuffer = TimeSpan.FromSeconds(30);
        else f.Interval = TimeSpan.FromSeconds(90);
        Assert.Empty(await f.Controller().ReadReceptionistSlotsAsync(f.Target, f.Date, 0, 30, default));
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("service")]
    [InlineData("duration")]
    [InlineData("staff")]
    public async Task OriginalBookingReaderRejectsInconsistentProviderMetadataWithoutCreatingAnything(string mismatch)
    {
        using var f = await CalendarFixture.CreateAsync();
        f.ProviderRecord = f.Appointment(f.Date, "10:00", "10:30", "approved-service");
        f.ProviderRecord.Id = "original-event";
        if (mismatch == "identity") f.ProviderRecord.Id = "different-event";
        if (mismatch == "service") f.ProviderRecord.ServiceId = "different-service";
        if (mismatch == "duration") f.ProviderRecord.EndDateTime = f.Wall(f.Date, "11:00");
        if (mismatch == "staff") f.ProviderRecord.StaffMemberIds = ["approved-staff", "another-staff"];
        Assert.Null(await f.Controller().ReadOriginalReceptionistSlotAsync(f.Target, "original-event", default));
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("2026-11-01T01:30:00", "America/New_York")]
    [InlineData("2026-03-08T02:30:00", "America/New_York")]
    public void AmbiguousOrNonexistentProviderWallTimeCannotAuthorizeABooking(string date, string zone) =>
        Assert.Null(CalendarController.ProviderUtc(new() { DateTime = date, TimeZone = zone }));

    private sealed class Fence : IReceptionistBookingExecutionFence
    {
        internal int Starts, Identities; internal bool FailAfterIdentity;
        public Task<bool> TryStartProviderAsync(CancellationToken ct) { Starts++; return Task.FromResult(Starts == 1); }
        public Task ProviderCreatedAsync(ReceptionistProviderBookingIdentity identity, CancellationToken ct)
        {
            Assert.Equal("original-event", identity.AppointmentId); Identities++;
            if (FailAfterIdentity) throw new IOException("Synthetic failure after preserving the original provider identity.");
            return Task.CompletedTask;
        }
    }
    private sealed class CalendarFixture : IDisposable
    {
        internal readonly DateOnly Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        internal DbContextOptions<MasterAppDbContext> Options = null!;
        internal ReceptionistTrustedBookingTarget Target = null!;
        internal List<BookingAppointment> Busy = [];
        internal List<string> ReadRequests = [];
        internal JsonElement? Sent;
        internal BookingAppointment? ProviderRecord;
        internal int Posts;
        internal bool NoPostRetry, ServicesOnSecondPage, MissingCalendarPage, MissingFirstPage;
        internal TimeSpan FreshPreBuffer;
        internal TimeSpan Interval = TimeSpan.FromMinutes(30);
        internal TaskCompletionSource? PostEntered, ReleasePost;
        internal readonly AppointmentFailure LocalFailure = new();
        private readonly List<ReceptionistTrustedBookingTarget> authorizedTargets = [];
        private readonly List<MasterAppDbContext> contexts = [];
        private ServiceProvider services = null!;
        private IMicrosoftCalendarConnectionAuthority connections = null!;
        internal static async Task<CalendarFixture> CreateAsync()
        {
            var f = new CalendarFixture();
            f.Options = new DbContextOptionsBuilder<MasterAppDbContext>().UseInMemoryDatabase("booking-gateway-" + Guid.NewGuid())
                .AddInterceptors(f.LocalFailure)
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options;
            var owner = MarketingOwnerScope.Agent(Guid.NewGuid()); var actor = "synthetic-booking-agent"; var profileId = Guid.NewGuid(); var intake = Guid.NewGuid();
            var configuration = new ConfigurationBuilder().Build();
            await using (var db = new MasterAppDbContext(f.Options))
            {
                db.AgentProfiles.Add(new() { Id = profileId, AgentUserId = actor, IsActive = true, BookingEnabled = true, BookingPageIdOrMailbox = "synthetic-mailbox" });
                db.AgentTrackingProfiles.Add(new() { Id = owner.AgentTrackingProfileId!.Value, AgentUserId = actor, AgentUpn = "synthetic-agent@example.invalid", Slug = "synthetic-booking", Status = "active" });
                db.WorkstationLeadProfiles.Add(new() { LeadId = "synthetic-lead", AgentUserId = actor, Bucket = "ServiceInquiry",
                    FirstName = "Synthetic", LastName = "Caller", Email = "invitation@example.invalid", Phone = "5550129999" });
                db.WebsiteLeadIntakeLinks.Add(new() { Id = intake, WebsiteLeadRowId = 100, WebsiteLeadPublicId = Guid.NewGuid(),
                    WorkstationLeadId = "synthetic-lead", AgentUserId = actor, Bucket = "ServiceInquiry" });
                db.MarketingConnections.Add(new() { OwnerKey = owner.Key, OwnerType = owner.OwnerType, AgentTrackingProfileId = owner.AgentTrackingProfileId,
                    Provider = MicrosoftCalendarConnectionAuthority.Provider, ConnectedUtc = DateTime.UtcNow.AddDays(-1),
                    ProviderAuthorizationMethod = MicrosoftCalendarConnectionAuthority.ApplicationAuthorization,
                    ProviderPermissionsJson = """["Bookings.Read.All","BookingsAppointment.ReadWrite.All"]""" });
                await db.SaveChangesAsync();
                var metadata = (await ReceptionistBookingReadinessReader.ReadCalendarAsync(db, owner, default))!;
                f.Target = new(owner, intake, "synthetic-lead", profileId, new() { ServiceKey = "website_services", BookingServiceId = "approved-service", TimeZoneId = "UTC", DurationMinutes = 30 },
                    metadata.Fingerprint, ReceptionistBookingReadinessReader.TargetFingerprint(owner, actor, "synthetic-mailbox"), new string('f', 64), "invitation@example.invalid", "Synthetic Caller");
            }
            var adapter = new Mock<IRequestAdapter>(MockBehavior.Strict);
            adapter.Setup(x => x.EnableBackingStore(It.IsAny<Microsoft.Kiota.Abstractions.Store.IBackingStoreFactory>()));
            adapter.SetupGet(x => x.BaseUrl).Returns("https://graph.microsoft.com/v1.0");
            adapter.SetupGet(x => x.SerializationWriterFactory).Returns(new JsonSerializationWriterFactory());
            adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<BookingServiceCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((RequestInformation request, ParsableFactory<BookingServiceCollectionResponse> _, Dictionary<string, ParsableFactory<IParsable>> __, CancellationToken ___) =>
                {
                    var chosen = f.Service(); chosen.PreBuffer = f.FreshPreBuffer;
                    var other = f.Service(); other.Id = "other-service"; other.PostBuffer = TimeSpan.FromMinutes(30); other.IsHiddenFromCustomers = true;
                    return f.ServicesOnSecondPage && !request.URI.ToString().Contains("page=2")
                        ? new() { Value = [other], OdataNextLink = "https://graph.microsoft.com/v1.0/solutions/bookingBusinesses/synthetic-mailbox/services?page=2" }
                        : new() { Value = f.ServicesOnSecondPage ? [chosen] : [chosen, other] };
                });
            adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<BookingService>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => f.Service());
            adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<GetStaffAvailabilityPostResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetStaffAvailabilityPostResponse { Value = [
                    new() { StaffId = "other-staff", AvailabilityItems = [new() { Status = BookingsAvailabilityStatus.Available, StartDateTime = f.Wall(f.Date, "08:00"), EndDateTime = f.Wall(f.Date, "10:00") }] },
                    new() { StaffId = "approved-staff", AvailabilityItems = [new() { ServiceId = "approved-service", Status = BookingsAvailabilityStatus.Available,
                        StartDateTime = f.Wall(f.Date, "10:00"), EndDateTime = f.Wall(f.Date, "12:00") }] }] });
            adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<BookingAppointmentCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((RequestInformation request, ParsableFactory<BookingAppointmentCollectionResponse> _, Dictionary<string, ParsableFactory<IParsable>> __, CancellationToken ___) =>
                {
                    f.ReadRequests.Add(request.URI.ToString());
                    if (f.MissingCalendarPage && (f.MissingFirstPage || request.URI.ToString().Contains("page=2"))) return new() { Value = null };
                    return new() { Value = f.Busy, OdataNextLink = f.MissingCalendarPage ? "https://graph.microsoft.com/v1.0/solutions/bookingBusinesses/synthetic-mailbox/calendarView?page=2" : null };
                });
            adapter.Setup(x => x.SendAsync(It.IsAny<RequestInformation>(), It.IsAny<ParsableFactory<BookingAppointment>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(), It.IsAny<CancellationToken>()))
                .Returns(async (RequestInformation request, ParsableFactory<BookingAppointment> _, Dictionary<string, ParsableFactory<IParsable>> __, CancellationToken ct) =>
                {
                    if (request.HttpMethod == Method.GET) { f.ReadRequests.Add(request.URI.ToString()); return f.ProviderRecord; }
                    Interlocked.Increment(ref f.Posts); f.NoPostRetry = request.RequestOptions.OfType<RetryHandlerOption>().Single().MaxRetry == 0;
                    f.PostEntered?.TrySetResult();
                    if (f.ReleasePost is not null) await f.ReleasePost.Task.WaitAsync(ct);
                    using var parsed = await JsonDocument.ParseAsync(request.Content!, cancellationToken: ct); f.Sent = parsed.RootElement.Clone();
                    var root = f.Sent.Value;
                    return f.ProviderRecord = new() { Id = "original-event", ServiceId = "approved-service", StaffMemberIds = ["approved-staff"],
                        CustomerName = root.GetProperty("customerName").GetString(), CustomerEmailAddress = root.GetProperty("customerEmailAddress").GetString(),
                        Customers = [new BookingCustomerInformation { EmailAddress = root.GetProperty("customerEmailAddress").GetString() }],
                        StartDateTime = new() { DateTime = root.GetProperty("startDateTime").GetProperty("dateTime").GetString(), TimeZone = "UTC" },
                        EndDateTime = new() { DateTime = root.GetProperty("endDateTime").GetProperty("dateTime").GetString(), TimeZone = "UTC" },
                        OptOutOfCustomerEmail = false, SmsNotificationsEnabled = false };
                });
            var graph = new GraphServiceClient(adapter.Object);
            var connections = new Mock<IMicrosoftCalendarConnectionAuthority>(MockBehavior.Strict);
            f.authorizedTargets.Add(f.Target);
            connections.Setup(x => x.CreateReviewedBookingClientAsync(It.IsAny<MarketingOwnerScope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MarketingOwnerScope scope, string fingerprint, CancellationToken _) =>
                { Assert.Contains(f.authorizedTargets, x => x.Owner == scope && x.CalendarConfigurationSha256 == fingerprint); return graph; });
            f.connections = connections.Object;
            f.services = new ServiceCollection().AddSingleton(f.Options).AddSingleton<IConfiguration>(configuration)
                .AddSingleton(new ReceptionistBookingReadinessReader(f.Options, configuration)).BuildServiceProvider();
            return f;
        }
        internal async Task<ReceptionistTrustedBookingTarget> AddSecondActorAsync()
        {
            var owner = MarketingOwnerScope.Agent(Guid.NewGuid()); var profileId = Guid.NewGuid(); var intake = Guid.NewGuid();
            const string actor = "synthetic-other-booking-agent", mailbox = "SYNTHETIC-MAILBOX/", lead = "synthetic-other-lead";
            await using var db = new MasterAppDbContext(Options);
            db.AgentProfiles.Add(new() { Id = profileId, AgentUserId = actor, IsActive = true, BookingEnabled = true, BookingPageIdOrMailbox = mailbox });
            db.AgentTrackingProfiles.Add(new() { Id = owner.AgentTrackingProfileId!.Value, AgentUserId = actor, AgentUpn = "second-agent@example.invalid", Slug = "second-booking", Status = "active" });
            db.WorkstationLeadProfiles.Add(new() { LeadId = lead, AgentUserId = actor, Bucket = "ServiceInquiry",
                FirstName = "Second Synthetic", LastName = "Caller", Email = "second-invitation@example.invalid" });
            db.WebsiteLeadIntakeLinks.Add(new() { Id = intake, WebsiteLeadRowId = 101, WebsiteLeadPublicId = Guid.NewGuid(),
                WorkstationLeadId = lead, AgentUserId = actor, Bucket = "ServiceInquiry" });
            db.MarketingConnections.Add(new() { OwnerKey = owner.Key, OwnerType = owner.OwnerType, AgentTrackingProfileId = owner.AgentTrackingProfileId,
                Provider = MicrosoftCalendarConnectionAuthority.Provider, ConnectedUtc = DateTime.UtcNow.AddDays(-1),
                ProviderAuthorizationMethod = MicrosoftCalendarConnectionAuthority.ApplicationAuthorization,
                ProviderPermissionsJson = """["Bookings.Read.All","BookingsAppointment.ReadWrite.All"]""" });
            await db.SaveChangesAsync();
            var metadata = (await ReceptionistBookingReadinessReader.ReadCalendarAsync(db, owner, default))!;
            var target = new ReceptionistTrustedBookingTarget(owner, intake, lead, profileId, Target.Service,
                metadata.Fingerprint, ReceptionistBookingReadinessReader.TargetFingerprint(owner, actor, mailbox),
                new string('e', 64), "second-invitation@example.invalid", "Second Synthetic Caller");
            authorizedTargets.Add(target); return target;
        }
        private BookingService Service() => new() { Id = "approved-service", DisplayName = "Synthetic approved service", IsHiddenFromCustomers = false,
            DefaultDuration = TimeSpan.FromMinutes(30), StaffMemberIds = ["approved-staff"], SchedulingPolicy = new() { TimeSlotInterval = Interval } };
        internal DateTimeTimeZone Wall(DateOnly date, string time) => new() { DateTime = date.ToString("yyyy-MM-dd") + "T" + time + ":00", TimeZone = "UTC" };
        internal BookingAppointment Appointment(DateOnly date, string start, string end, string service) => new()
        { Id = "busy-" + service, ServiceId = service, StaffMemberIds = ["approved-staff"], StartDateTime = Wall(date, start), EndDateTime = Wall(date, end) };
        internal CalendarController Controller()
        {
            var db = new MasterAppDbContext(Options); contexts.Add(db);
            var zone = new Mock<IAgentTimeZoneResolver>(MockBehavior.Strict);
            return new(NullLogger<CalendarController>.Instance, db, zone.Object, connections)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        }
        public void Dispose() { foreach (var db in contexts) db.Dispose(); services.Dispose(); }
    }

    private sealed class AppointmentFailure : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        internal string? Mode;
        internal int Failures;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Mode == "before" && data.Context!.ChangeTracker.Entries<LeadAppointment>().Any(x => x.State == EntityState.Added))
            { Mode = null; Failures++; throw new DbUpdateException("Synthetic local appointment commit failure."); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData data,
            int result, CancellationToken ct = default)
        {
            if (Mode == "after" && data.Context!.ChangeTracker.Entries<LeadAppointment>().Any())
            { Mode = null; Failures++; throw new IOException("Synthetic lost local appointment commit acknowledgement."); }
            return ValueTask.FromResult(result);
        }
    }
}
