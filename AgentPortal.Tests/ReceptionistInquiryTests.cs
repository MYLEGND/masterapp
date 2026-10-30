using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Data.Configurations;
using Infrastructure.Leads;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>Actual inquiry/CRM writers and fenced action authority, with synthetic provider receipts and no network transport.</summary>
public sealed partial class ReceptionistInquiryTests
{
    [Fact]
    public async Task ControlledQaOutcomeIsDurableReadBackIndependentlyAndCreatesNoCustomerRecords()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(controlledTest: true); var facts = Facts();
        var action = await f.ClaimAsync(call, facts);
        Assert.Null(await f.Inquiries.ReadSafeOutcomeAsync(call, action));
        var result = await f.Inquiries.PersistQaOutcomeAsync(call, action, facts, Receipt);
        Assert.True(result.Created); Assert.Equal("qa_outcome_saved", result.State);
        Assert.Null(result.InquiryId); Assert.Equal(0, result.Counting.NewGenuineInquiryCount);
        Assert.Equal(0, result.Counting.NewVerifiedOrganicInquiryCount);
        var read = await f.Inquiries.ReadSafeOutcomeAsync(call, action);
        Assert.NotNull(read); Assert.Equal("qa_outcome_verified", read.State); Assert.True(read.ControlledQa);
        Assert.DoesNotContain(facts.Email!, JsonSerializer.Serialize(read));
        Assert.DoesNotContain(facts.RequestSummary!, JsonSerializer.Serialize(read));
        Assert.False((await f.Inquiries.PersistQaOutcomeAsync(call, action, facts, Receipt)).Created);
        Assert.Null(await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action));
        await using var db = f.Database();
        var committed = Assert.Single(await db.Set<ReceptionistActionIntent>().AsNoTracking().ToListAsync());
        Assert.Equal(ReceptionistInquiryAuthority.CanonicalArgumentsSha256(facts), committed.ArgumentsSha256);
        Assert.Equal("verified", committed.State); Assert.StartsWith("receptionist-qa:", committed.ActualRecordReference);
        Assert.Empty(await db.WebsiteLeads.ToListAsync()); Assert.Empty(await db.WorkstationLeadProfiles.ToListAsync());
        Assert.Empty(await db.WebsiteLeadIntakeLinks.ToListAsync()); Assert.Empty(await db.LeadAppointments.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentQaReplayHasOneDurableOutcomeAndOneWriteAcknowledgement()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(controlledTest: true); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            f.Inquiries.PersistQaOutcomeAsync(call, action, facts, Receipt)));
        Assert.Single(outcomes.Where(x => x.Created));
        Assert.Single(outcomes.Select(x => x.RecordReference).Distinct());
        Assert.Equal("qa_outcome_verified", (await f.Inquiries.ReadSafeOutcomeAsync(call, action))!.State);
        await using var db = f.Database(); Assert.Single(await db.Set<ReceptionistActionIntent>().ToListAsync());
        Assert.Empty(await db.WebsiteLeads.ToListAsync()); Assert.Empty(await db.WorkstationLeadProfiles.ToListAsync());
    }

    [Fact]
    public async Task QaReceiptCannotAuthorizeChangedFactsScopeOrAProductionCall()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(controlledTest: true); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(call, action, facts with { Email = "changed@example.invalid" }, Receipt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(call, action, facts, "different-confirmation"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(call with { Owner = MarketingOwnerScope.Business(Guid.NewGuid()) }, action, facts, Receipt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(call with { ConfigurationId = Guid.NewGuid() }, action, facts, Receipt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(call with { ControlledTest = false }, action, facts, Receipt));
        var production = await f.CallAsync(); var productionAction = await f.ClaimAsync(production, facts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(production, productionAction, facts, Receipt));
        await using var db = f.Database(); Assert.Empty(await db.WebsiteLeads.ToListAsync());
    }

    [Fact]
    public async Task UncertainQaActionIsNotReexecutedAndTamperedCommittedReceiptIsRejected()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(controlledTest: true); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await f.Admission.RecordActionOutcomeAsync(action, "uncertain", null, "synthetic-uncertainty");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistQaOutcomeAsync(call, action, facts, Receipt));
        Assert.Null(await f.Inquiries.ReadSafeOutcomeAsync(call, action));
        var other = await f.CallAsync(controlledTest: true); var otherAction = await f.ClaimAsync(other, facts);
        await f.Inquiries.PersistQaOutcomeAsync(other, otherAction, facts, Receipt);
        await using (var db = f.Database())
        {
            var row = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == otherAction);
            row.VerificationReceipt = "qa-intake-outcome:v1:tampered"; await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.ReadSafeOutcomeAsync(other, otherAction));
    }

    [Fact]
    public async Task CallerProvidedFullNamePersistsWithoutAnInferredFamilyName()
    {
        await using var f = await Fixture.CreateAsync(); var call = await f.CallAsync();
        var facts = Facts() with { FirstName = null, LastName = null, CallerName = "Jordan Riley" };
        var action = await f.ClaimAsync(call, facts);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        var read = (await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action))!;
        Assert.Equal("Jordan Riley", read.ConfirmedFacts.CallerName);
        Assert.Null(read.ConfirmedFacts.FirstName); Assert.Null(read.ConfirmedFacts.LastName);
        await using var db = f.Database();
        var crm = Assert.Single(await db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal("Jordan Riley", crm.FirstName); Assert.Equal("", crm.LastName);
    }

    [Fact]
    public async Task FounderServiceInquiryCommitsCanonicalRecordsAndRequiresIndependentReadback()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync();
        var facts = Facts();
        var action = await f.ClaimAsync(call, facts);
        var saved = await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.Equal("canonical_intake_saved", saved.State);
        Assert.True(saved.Created); Assert.NotNull(saved.InquiryId);
        var read = await f.Inquiries.ReadBackAsync(MarketingOwnerScope.Founder, call.CallId, action);
        Assert.NotNull(read); Assert.Equal(saved.InquiryId, read.InquiryId);
        Assert.Equal("crm_readback_verified", read.State);
        Assert.Equal("founder", read.OwnerKey);
        Assert.StartsWith("inquiry-readback:v1:", read.VerificationReceipt);
        Assert.Equal("owner_notification_disabled", read.DeliveryState);
        Assert.Null(read.Counting.NewGenuineInquiryCount); Assert.Null(read.Counting.NewVerifiedOrganicInquiryCount);
        Assert.Equal("pending", read.Counting.DeduplicationState);
        await using var db = f.Database();
        var lead = Assert.Single(await db.WebsiteLeads.ToListAsync());
        var crm = Assert.Single(await db.WorkstationLeadProfiles.ToListAsync());
        var link = Assert.Single(await db.WebsiteLeadIntakeLinks.ToListAsync());
        Assert.Null(lead.AgentTrackingProfileId); Assert.Null(lead.CommerceBusinessId);
        Assert.Null(crm.CommerceBusinessId); Assert.Equal("founder-actor", crm.AgentUserId);
        Assert.Equal(WorkstationLeadBuckets.ServiceInquiry, crm.Bucket);
        Assert.Equal(WorkstationLeadBuckets.ServiceInquiry, crm.OriginalLeadType);
        Assert.Equal(lead.LeadId.ToString("N"), crm.LeadId);
        Assert.Equal(crm.LeadId, link.WorkstationLeadId);
        Assert.Equal(lead.MetadataJson, link.SnapshotJson);
        Assert.Contains("Stated Need", link.DiscoverySummaryJson);
        Assert.Null(lead.SourcePageKey); Assert.Null(lead.SourceCtaKey); Assert.Null(lead.Host);
        Assert.Null(lead.WebsiteContentVersionId); Assert.Null(lead.WebsiteBindingId);
        Assert.Null(lead.SessionId); Assert.Null(lead.VisitorId); Assert.Null(lead.ClientIpAddress); Assert.Null(lead.ClientUserAgent);
        Assert.False(lead.MarketingEmailConsent); Assert.False(lead.CallTextConsent); Assert.False(lead.TermsAccepted);
        Assert.DoesNotContain("MetaTracking", lead.MetadataJson);
        Assert.Empty(await db.Set<CommerceWebsiteInquiry>().ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingContactFactsRemainAbsentAndContactabilityIsExplicit(bool phoneOnly)
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync();
        var facts = Facts() with
        {
            FirstName = null, LastName = null, Email = null,
            Phone = phoneOnly ? "+1 (202) 555-0191" : null,
            ContactStatus = phoneOnly ? "caller_confirmed_for_requested_purpose" : "declined",
            RequestedContactMethod = phoneOnly ? "callback_requested" : "inbound_voice_only",
            ContactPermissionState = phoneOnly ? "caller_requested_purpose_only" : "declined",
            ContactPurpose = phoneOnly ? "Discuss the requested website work" : null
        };
        var action = await f.ClaimAsync(call, facts);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        var read = (await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action))!;
        Assert.Equal(phoneOnly, read.Counting.Contactable);
        Assert.Null(read.ConfirmedFacts.Email); Assert.Null(read.ConfirmedFacts.FirstName);
        await using var db = f.Database();
        var lead = Assert.Single(await db.WebsiteLeads.ToListAsync());
        var crm = Assert.Single(await db.WorkstationLeadProfiles.ToListAsync());
        Assert.Equal("", lead.Email); Assert.Equal("", lead.FirstName);
        Assert.Equal("", crm.Email); Assert.Equal("", crm.FirstName);
        Assert.Equal(phoneOnly ? "2025550191" : "", crm.Phone);
        Assert.False(lead.CallTextConsent);
    }

    [Fact]
    public async Task ConcurrentAndRestartedReplaysCreateOneInquiryCrmProfileAndIntakeLink()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        using var start = new ManualResetEventSlim(false);
        var writes = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(writes);
        Assert.Single(results.Where(x => x.Created));
        Assert.Single(results.Select(x => x.InquiryId).Distinct());
        var restart = await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.False(restart.Created);
        await using var db = f.Database();
        Assert.Single(await db.WebsiteLeads.ToListAsync());
        Assert.Single(await db.WorkstationLeadProfiles.ToListAsync());
        Assert.Single(await db.WebsiteLeadIntakeLinks.ToListAsync());
    }

    [Fact]
    public async Task ChangedFactsOrConfirmationCannotReuseAClaimedToken()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistConfirmedAsync(
            call, action, facts with { Email = "different@example.invalid" }, Receipt));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistConfirmedAsync(call, action, facts, "other-confirmation"));
        await using var db = f.Database(); Assert.Empty(await db.WebsiteLeads.ToListAsync());
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistConfirmedAsync(
            call, action, facts with { RequestSummary = "Changed after confirmation" }, Receipt));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("project")]
    [InlineData("session")]
    [InlineData("configuration")]
    public async Task SuppliedContextCannotOverrideTrustedCallIdentity(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        var wrong = change switch
        {
            "owner" => call with { Owner = MarketingOwnerScope.Business(Guid.NewGuid()) },
            "project" => call with { ProviderProjectKey = "other-project" },
            "session" => call with { ProviderSessionId = "other-session" },
            _ => call with { ConfigurationId = Guid.NewGuid() }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistConfirmedAsync(wrong, action, facts, Receipt));
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.Null(await f.Inquiries.ReadBackAsync(MarketingOwnerScope.Agent(Guid.NewGuid()), call.CallId, action));
        Assert.Null(await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, Guid.NewGuid()));
    }

    [Fact]
    public async Task AClaimedWriteCanFinishAfterDisconnectButAnUncertainUncommittedActionCannotExecute()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        f.Clock.Now = f.Clock.Now.AddMinutes(3);
        await f.Admission.RecordProviderStateAsync(call.CallId, "closed", "synthetic-closure", f.Clock.Now);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.NotNull(await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action));
        await using var other = await Fixture.CreateAsync();
        var otherCall = await other.CallAsync(); var otherAction = await other.ClaimAsync(otherCall, facts);
        await other.Admission.RecordActionOutcomeAsync(otherAction, "uncertain", null, "synthetic-interruption");
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.Inquiries.PersistConfirmedAsync(otherCall, otherAction, facts, Receipt));
        Assert.Null(await other.Inquiries.ReadBackAsync(otherCall.Owner, otherCall.CallId, otherAction));
    }

    [Theory]
    [InlineData("qa_test")]
    [InlineData("wrong_number")]
    [InlineData("spam_or_vendor")]
    [InlineData("engagement_only")]
    [InlineData("existing_customer_service")]
    [InlineData("existing_opportunity_followup")]
    [InlineData("unknown")]
    public async Task NonInquiriesHaveNoInventedRecordAndZeroNewInquiryCounts(string classification)
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync();
        var facts = new ConfirmedReceptionistInquiry { AcquisitionClass = classification };
        var action = await f.ClaimAsync(call, facts);
        var result = await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.Equal("not_attempted", result.State); Assert.Null(result.InquiryId); Assert.Null(result.RecordReference);
        Assert.Equal(0, result.Counting.NewGenuineInquiryCount); Assert.Equal(0, result.Counting.NewVerifiedOrganicInquiryCount);
        await using var db = f.Database();
        Assert.Empty(await db.WebsiteLeads.ToListAsync()); Assert.Empty(await db.WorkstationLeadProfiles.ToListAsync());
    }

    [Fact]
    public async Task TrustedControlledTestOverridesGenuineClassification()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(controlledTest: true); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        var result = await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.Equal("qa_test", result.ExclusionReason); Assert.Null(result.InquiryId);
        Assert.Equal(0, result.Counting.NewGenuineInquiryCount);
    }

    [Fact]
    public async Task SimilarContactDoesNotMutateExistingCrmIdentityOrEstablishNewPersonStatus()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts();
        await using (var db = f.Database())
        {
            db.WorkstationLeadProfiles.Add(new()
            {
                LeadId = "preexisting-contact", AgentUserId = "founder-actor", Bucket = "FollowUp",
                OriginalLeadType = WorkstationLeadBuckets.ServiceInquiry, FirstName = "Existing", LastName = "Identity",
                Email = facts.Email!, Phone = "2025550191", CrmNotes = "Existing authorized private notes", CrmStatus = "Prospect"
            });
            await db.SaveChangesAsync();
        }
        var action = await f.ClaimAsync(call, facts);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        await using var read = f.Database();
        var prior = await read.WorkstationLeadProfiles.SingleAsync(x => x.LeadId == "preexisting-contact");
        Assert.Equal("Existing", prior.FirstName); Assert.Equal("Identity", prior.LastName);
        Assert.Equal("FollowUp", prior.Bucket); Assert.Equal("Prospect", prior.CrmStatus);
        Assert.Equal("Existing authorized private notes", prior.CrmNotes);
        Assert.Equal(2, await read.WorkstationLeadProfiles.CountAsync());
        var receipt = (await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action))!;
        Assert.NotEqual(prior.LeadId, receipt.CrmRecordReference);
        Assert.Null(receipt.Counting.NewGenuineInquiryCount);
    }

    [Fact]
    public async Task BusinessPhoneOnlyActionsDoNotCollideOnAnAbsentEmailOrCrossOwner()
    {
        await using var f = await Fixture.CreateAsync(MarketingOwnerScope.Business(Guid.NewGuid()));
        var facts = Facts() with { Email = null, RequestedContactMethod = "callback_requested" };
        var first = await f.CallAsync(); var second = await f.CallAsync();
        var firstAction = await f.ClaimAsync(first, facts); var secondAction = await f.ClaimAsync(second, facts);
        await f.Inquiries.PersistConfirmedAsync(first, firstAction, facts, Receipt);
        await f.Inquiries.PersistConfirmedAsync(second, secondAction, facts, Receipt);
        await using var db = f.Database();
        Assert.Equal(2, await db.WorkstationLeadProfiles.CountAsync());
        Assert.All(await db.WorkstationLeadProfiles.ToListAsync(), x =>
        { Assert.Equal(first.Owner.CommerceBusinessId, x.CommerceBusinessId); Assert.Equal("", x.AgentUserId); Assert.Equal("", x.Email); });
        Assert.Null(await f.Inquiries.ReadBackAsync(MarketingOwnerScope.Founder, first.CallId, firstAction));
    }

    [Fact]
    public async Task AgentInquiryUsesTheExistingTrackingOwnerAndIndependentScopedReadback()
    {
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        await using var f = await Fixture.CreateAsync(owner);
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        var read = (await f.Inquiries.ReadBackAsync(owner, call.CallId, action))!;
        Assert.Equal(owner.Key, read.OwnerKey);
        Assert.Null(await f.Inquiries.ReadBackAsync(MarketingOwnerScope.Founder, call.CallId, action));
        await using var db = f.Database();
        Assert.Equal(owner.AgentTrackingProfileId, (await db.WebsiteLeads.SingleAsync()).AgentTrackingProfileId);
        Assert.Equal("agent-actor", (await db.WorkstationLeadProfiles.SingleAsync()).AgentUserId);
    }

    [Fact]
    public async Task LostCommitAcknowledgementIsRecoveredByIndependentReadbackWithoutAnotherWrite()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        f.Options = new DbContextOptionsBuilder<MasterAppDbContext>(f.Options)
            .AddInterceptors(new LostCommitAcknowledgement()).Options;
        await Assert.ThrowsAsync<IOException>(() => f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt));
        await f.Admission.RecordActionOutcomeAsync(action, "uncertain", null, "synthetic-lost-commit-response");
        var read = (await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action))!;
        Assert.Equal("crm_readback_verified", read.State);
        await f.Admission.RecordActionOutcomeAsync(action, "verified", read.RecordReference, read.VerificationReceipt);
        var replay = await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        Assert.False(replay.Created); Assert.Equal(read.InquiryId, replay.InquiryId);
        await using var db = f.Database();
        Assert.Single(await db.WebsiteLeads.ToListAsync());
        Assert.Single(await db.WorkstationLeadProfiles.ToListAsync());
        Assert.Single(await db.WebsiteLeadIntakeLinks.ToListAsync());
    }

    [Fact]
    public async Task FailedCanonicalIntakeLinkRollsBackInquiryAndCrmTogether()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await using (var db = f.Database())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_intake BEFORE INSERT ON WebsiteLeadIntakeLinks BEGIN SELECT RAISE(ABORT, 'synthetic failed handoff'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt));
        await using var read = f.Database();
        Assert.Empty(await read.WebsiteLeads.ToListAsync()); Assert.Empty(await read.WorkstationLeadProfiles.ToListAsync());
        Assert.Empty(await read.WebsiteLeadIntakeLinks.ToListAsync());
        Assert.Null(await f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action));
    }

    [Theory]
    [InlineData("facts")]
    [InlineData("link")]
    [InlineData("owner")]
    [InlineData("crm-first-name")]
    [InlineData("crm-last-name")]
    [InlineData("crm-email")]
    [InlineData("crm-phone")]
    [InlineData("crm-product")]
    [InlineData("crm-bucket")]
    [InlineData("same-owner-link-substitution")]
    public async Task IndependentReadbackRejectsChangedDatabaseFactsOrMissingHandoff(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        await using (var db = f.Database())
        {
            if (change == "facts") (await db.WebsiteLeads.SingleAsync()).Notes = "A different unconfirmed need";
            else if (change == "link") db.WebsiteLeadIntakeLinks.Remove(await db.WebsiteLeadIntakeLinks.SingleAsync());
            else
            {
                var crm = await db.WorkstationLeadProfiles.SingleAsync();
                switch (change)
                {
                    case "owner": crm.AgentUserId = "different-owner"; break;
                    case "crm-first-name": crm.FirstName = "Unconfirmed"; break;
                    case "crm-last-name": crm.LastName = "Different"; break;
                    case "crm-email": crm.Email = "unconfirmed@example.invalid"; break;
                    case "crm-phone": crm.Phone = "2025550123"; break;
                    case "crm-product": crm.OriginalLeadType = WorkstationLeadBuckets.LifeInsurance; break;
                    case "crm-bucket": crm.Bucket = WorkstationLeadBuckets.LifeInsurance; break;
                    case "same-owner-link-substitution":
                        db.WorkstationLeadProfiles.Add(new()
                        {
                            LeadId = "another-same-owner-record", AgentUserId = crm.AgentUserId,
                            CommerceBusinessId = crm.CommerceBusinessId, Bucket = crm.Bucket, OriginalLeadType = crm.OriginalLeadType,
                            FirstName = crm.FirstName, LastName = crm.LastName, Email = crm.Email, Phone = crm.Phone
                        });
                        (await db.WebsiteLeadIntakeLinks.SingleAsync()).WorkstationLeadId = "another-same-owner-record";
                        break;
                }
            }
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.ReadBackAsync(call.Owner, call.CallId, action));
    }

    [Fact]
    public async Task OwnerNotificationRequiresPinnedApprovalAndDeliveryIsNotAWriteReceipt()
    {
        await using var f = await Fixture.CreateAsync();
        var call = await f.CallAsync(); var facts = Facts(); var action = await f.ClaimAsync(call, facts);
        await f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt);
        await using var db = f.Database(); var lead = await db.WebsiteLeads.SingleAsync();
        var sends = 0;
        var result = await WebsiteLeadNotificationAuthority.DeliverAsync(db, lead, "owner@example.invalid", _ =>
        { sends++; return Task.FromResult(true); });
        Assert.Equal(0, sends); Assert.False(result.Claimed); Assert.False(result.Sent);
        Assert.Null(lead.NotificationAttemptUtc); Assert.Equal("New confirmed voice inquiry", WebsiteLeadEmailTemplate.SubjectFor(lead));
        await using var enabled = await Fixture.CreateAsync(notifyOwner: true);
        var allowedCall = await enabled.CallAsync(); var allowedAction = await enabled.ClaimAsync(allowedCall, facts);
        await enabled.Inquiries.PersistConfirmedAsync(allowedCall, allowedAction, facts, Receipt);
        var read = (await enabled.Inquiries.ReadBackAsync(allowedCall.Owner, allowedCall.CallId, allowedAction))!;
        Assert.Equal("owner_notification_pending", read.DeliveryState);
        await using var allowedDb = enabled.Database();
        Assert.True(ReceptionistInquiryAuthority.OwnerNotificationsEnabled(await allowedDb.WebsiteLeads.SingleAsync()));
        Assert.Null((await allowedDb.WebsiteLeads.SingleAsync()).NotificationAttemptUtc);
    }

    [Fact]
    public async Task UnknownBucketUnconfirmedContactAndUnfencedActionFailClosed()
    {
        await using var f = await Fixture.CreateAsync(); var call = await f.CallAsync(); var facts = Facts();
        Assert.Throws<ArgumentException>(() => ReceptionistInquiryAuthority.CanonicalArgumentsSha256(facts with { ContactStatus = "uncertain" }));
        Assert.Throws<ArgumentException>(() => ReceptionistInquiryAuthority.CanonicalArgumentsSha256(facts with { RequestSummary = null }));
        Assert.Throws<ArgumentException>(() => ReceptionistInquiryAuthority.ReadIntakeRules(call with
        { Configuration = call.Configuration with { IntakeRulesJson = "{\"canonicalInquiry\":{\"services\":[{\"serviceKey\":\"website\",\"crmBucket\":\"invented\"}]}}" } }));
        var action = await f.ClaimAsync(call, facts);
        await using (var db = f.Database())
        {
            var row = await db.Set<ReceptionistActionIntent>().SingleAsync(x => x.Id == action);
            row.ExecutionLeaseToken = null; row.ExecutionFence = 0; row.WorkerId = null;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Inquiries.PersistConfirmedAsync(call, action, facts, Receipt));
    }

    [Fact]
    public void ChannelIdentityIsScopedAndWebSubmissionIdentityRulesRemainExact()
    {
        var token = Guid.NewGuid(); var call = Guid.NewGuid();
        var lead = new WebsiteLead { LeadId = Guid.NewGuid(), Email = " Mixed@Example.invalid ", SourcePageKey = "/contact" };
        var expected = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{token:D}||/contact|mixed@example.invalid")).AsSpan(0, 16));
        Assert.Equal(expected, WebsiteLeadSubmission.ResolveId(lead, token.ToString("D")));
        Assert.Equal(lead.LeadId, WebsiteLeadSubmission.ResolveId(lead, null));
        var identity = new ConfirmedInquirySubmissionIdentity(MarketingOwnerScope.Founder, "inbound_voice", "openai", "project", "session", call, token);
        var one = WebsiteLeadSubmission.ResolveConfirmedId(lead, identity);
        lead.Email = "correction@example.invalid";
        Assert.Equal(one, WebsiteLeadSubmission.ResolveConfirmedId(lead, identity));
        Assert.NotEqual(one, WebsiteLeadSubmission.ResolveConfirmedId(lead, identity with { ProviderProjectKey = "other-project" }));
        var business = MarketingOwnerScope.Business(Guid.NewGuid()); lead.CommerceBusinessId = business.CommerceBusinessId;
        Assert.NotEqual(one, WebsiteLeadSubmission.ResolveConfirmedId(lead, identity with { Owner = business }));
        Assert.Throws<ArgumentException>(() => WebsiteLeadSubmission.ResolveConfirmedId(lead, identity));
        Assert.Equal(WorkstationLeadBuckets.ServiceInquiry, WorkstationLeadBuckets.NormalizeBucket("Service Inquiry"));
        Assert.Contains(WorkstationLeadBuckets.ServiceInquiry, WorkstationLeadBuckets.ProductBuckets);
        Assert.False(WorkstationLeadBuckets.UsesRequestedAmountField(WorkstationLeadBuckets.ServiceInquiry));
        Assert.Equal(WorkstationLeadBuckets.LifeInsurance, WorkstationLeadBuckets.ResolveWebsiteLeadBucket("legacy-unknown"));
    }

    private const string Receipt = "synthetic-current-confirmation-receipt";
    private static ConfirmedReceptionistInquiry Facts() => new()
    {
        AcquisitionClass = "observed_genuine_inquiry_pending_reconciliation", ServiceKey = "website_services",
        RequestSummary = "Please discuss the confirmed website update scope.", FirstName = "Synthetic", LastName = "Caller",
        Email = "caller@example.invalid", Phone = "+1 202 555 0191", ContactStatus = "caller_confirmed_for_requested_purpose",
        RequestedContactMethod = "email_requested", ContactPermissionState = "caller_requested_purpose_only",
        ContactPurpose = "Discuss the requested website update", SourceStatement = "I do not remember how I found the number"
    };

    private sealed class TestClock : TimeProvider
    {
        internal DateTime Now = new(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "reception-inquiry-tests-" + Guid.NewGuid().ToString("N") + ".sqlite");
        internal DbContextOptions<MasterAppDbContext> Options = null!;
        internal TestClock Clock = new();
        internal MarketingOwnerScope Owner = MarketingOwnerScope.Founder;
        internal Guid AgentProfileId = Guid.NewGuid();
        internal readonly Dictionary<Guid, Guid> Leases = [];
        internal readonly Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider Protection = new();
        internal IConfiguration Configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new[] { new KeyValuePair<string, string?>("Founder:Email", "founder@example.invalid") }).Build();
        internal ReceptionistAdmissionService Admission => new(Options, Clock);
        internal ReceptionistInquiryAuthority Inquiries => new(Options, Admission, Configuration, NullLogger<WebsiteLifeLeadCaptureService>.Instance, Clock);
        internal MasterAppDbContext Database() => new(Options);

        internal static async Task<Fixture> CreateAsync(MarketingOwnerScope? owner = null, bool notifyOwner = false, bool bookingEnabled = false, bool qaOnly = true)
        {
            var f = new Fixture { Owner = owner ?? MarketingOwnerScope.Founder };
            var connection = "Data Source=" + f.path + ";Pooling=False;Default Timeout=20";
            await using var schema = new InquirySchemaContext(new DbContextOptionsBuilder().UseSqlite(connection).Options);
            f.Options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).UseModel(schema.Model).Options;
            await schema.Database.EnsureCreatedAsync();
            var funding = await f.OpenAsync("funding", "synthetic-shared-provider");
            await using var db = f.Database();
            if (f.Owner.CommerceBusinessId is { } business)
            {
                db.Add(new CommerceBusiness { Id = business, Key = "synthetic", DisplayName = "Synthetic business", OwnerEmail = "owner@example.invalid" });
                db.Add(new CommerceBusinessStorefrontSettings { CommerceBusinessId = business });
            }
            else
            {
                var actor = f.Owner.AgentTrackingProfileId.HasValue ? "agent-actor" : "founder-actor";
                var email = f.Owner.AgentTrackingProfileId.HasValue ? "agent@example.invalid" : "founder@example.invalid";
                db.Add(new AgentProfile { Id = f.AgentProfileId, AgentUserId = actor, AgentUpn = email, NormalizedEmail = email });
                if (f.Owner.AgentTrackingProfileId is { } agent)
                    db.Add(new AgentTrackingProfile { Id = agent, AgentUserId = actor, AgentUpn = email, Slug = "synthetic" });
            }
            var config = new ReceptionistBusinessConfiguration
            {
                BusinessName = "Synthetic owner", ConversationInstructions = "Use confirmed synthetic test facts only.", BusinessPolicy = "No inferred identity or consent.",
                TimeZoneId = "UTC", Currency = "USD", MonthlyCashLimit = 1000, MonthlyConsumptionLimit = 1000, MonthlyVoiceSecondsLimit = 10000,
                MaximumConcurrentCalls = 20, MaximumCallSeconds = 120, AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 100,
                Hours = Enum.GetValues<DayOfWeek>().Select(day => new ReceptionistHoursWindow(day, TimeOnly.MinValue, new TimeOnly(23, 59, 59))).ToArray(),
                RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake],
                IntakeRulesJson = JsonSerializer.Serialize(new { canonicalInquiry = new { services = new[] { new { serviceKey = "website_services", crmBucket = "ServiceInquiry" } }, notifyOwner } })
            };
            var binding = new ReceptionistProviderBinding
            {
                IngressKey = "synthetic-ingress", ProviderProjectKey = "synthetic-project", Model = "synthetic-approved-model", Voice = "synthetic-voice", BackendMode = "application_services", Transport = "sip",
                CredentialReference = "synthetic-secret-reference", WebhookSecretReference = "synthetic-webhook-reference", ProviderVerificationReceipt = "synthetic-provider-receipt",
                ExclusiveProjectVerificationReceipt = "synthetic-exclusive-project-receipt",
                CostVerificationReceipt = "synthetic-cost-receipt", DisclosureConfigurationReceipt = "synthetic-disclosure", RetentionConfigurationReceipt = "synthetic-retention",
                CarrierFallbackReceipt = "synthetic-fallback", MaximumSupportedCallSeconds = 120, ControlledTestOnly = false, PublicActivationReceipt = "synthetic-test-only-activation",
                Costs = [new("provider_cost", funding, 1m, 0m, 1m, 0m)],
                Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, ReceptionistInquiryAuthority.RouteKey, ReceptionistCapabilityStates.Accepted, "synthetic-intake-acceptance")]
            };
            if (bookingEnabled)
            {
                config = config with
                {
                    RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake, ReceptionistCapabilities.CanonicalBooking],
                    ServiceDefinitionsJson = """[{"serviceKey":"website_services","displayName":"Synthetic website consultation","aliases":["website"],"discoveryQuestion":"What is your website goal?","publicSummary":"Synthetic service information."}]""",
                    BookingPolicy = new() { Enabled = true, QaOnly = qaOnly, BookingAgentProfileId = f.AgentProfileId,
                        AllowConsentedCustomerEmail = true, PrivacyAcceptanceReference = "synthetic-reviewed-privacy",
                        ExpectedCalendarConfigurationSha256 = new('a', 64), ExpectedCalendarTargetSha256 = new('b', 64),
                        MinimumNoticeMinutes = 0, MaximumAdvanceDays = 30,
                        Services = [new() { ServiceKey = "website_services", BookingServiceId = "synthetic-service", TimeZoneId = "UTC", DurationMinutes = 30 }] }
                };
                binding = binding with { Capabilities = [.. binding.Capabilities,
                    new(ReceptionistCapabilities.CanonicalBooking, Infrastructure.Bookings.ReceptionistBookingAuthority.RouteKey,
                        ReceptionistCapabilityStates.Accepted, "synthetic-booking-acceptance")] };
            }
            var connectionRow = new MarketingConnection
            {
                OwnerKey = f.Owner.Key, OwnerType = f.Owner.OwnerType, AgentTrackingProfileId = f.Owner.AgentTrackingProfileId,
                CommerceBusinessId = f.Owner.CommerceBusinessId, Provider = "openai", ReceptionistIngressKey = binding.IngressKey,
                ReceptionistProviderProjectKey = binding.ProviderProjectKey, ReceptionistAdmissionEnabled = true
            };
            db.Add(connectionRow); await db.SaveChangesAsync();
            await f.OpenAsync("owner", f.Owner.Key);
            var configJson = ReceptionistConfigurationCodec.Serialize(config);
            var version = new ReceptionistConfigurationVersion
            {
                MarketingConnectionId = connectionRow.Id, Version = 1, ConfigurationJson = configJson,
                ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(configJson), AcceptedBindingJson = JsonSerializer.Serialize(binding, ReceptionistConfigurationCodec.Json),
                CreatedByActorId = "synthetic-owner", CreatedUtc = f.Clock.Now
            };
            db.Add(version); await db.SaveChangesAsync();
            connectionRow.ReceptionistActiveConfigurationId = version.Id; await db.SaveChangesAsync();
            return f;
        }

        private async Task<Guid> OpenAsync(string kind, string scope)
        {
            var id = await Admission.OpenBudgetPeriodAsync(new(kind, scope, "USD", "UTC", 2026, 2, 1000, 1000, 0, 0, 0,
                "synthetic-opening:" + scope, Clock.Now.AddTicks(-1)));
            var row = (await Admission.GetBudgetAccountAsync(id))!;
            await Admission.SetBudgetAdmissionAsync(id, row.Revision, true);
            return id;
        }

        internal async Task<ReceptionistCallContext> CallAsync(bool controlledTest = false)
        {
            var session = Guid.NewGuid().ToString("N");
            var binding = await Admission.GetIngressBindingAsync("openai", "synthetic-ingress");
            Assert.NotNull(binding);
            if (binding.Binding.ControlledTestOnly != controlledTest)
            {
                await using var db = Database();
                var connection = await db.MarketingConnections.SingleAsync(x => x.Id == binding.MarketingConnectionId);
                var original = await db.Set<ReceptionistConfigurationVersion>().AsNoTracking().SingleAsync(x => x.Id == binding.ConfigurationId);
                var changed = binding.Binding with { ControlledTestOnly = controlledTest,
                    PublicActivationReceipt = controlledTest ? null : "synthetic-test-only-activation" };
                var version = new ReceptionistConfigurationVersion
                {
                    MarketingConnectionId = original.MarketingConnectionId, Version = original.Version + 1,
                    ConfigurationJson = original.ConfigurationJson, ConfigurationSha256 = original.ConfigurationSha256,
                    AcceptedBindingJson = JsonSerializer.Serialize(changed, ReceptionistConfigurationCodec.Json),
                    CreatedByActorId = "synthetic-owner", CreatedUtc = Clock.Now
                };
                db.Add(version); await db.SaveChangesAsync(); connection.ReceptionistActiveConfigurationId = version.Id; await db.SaveChangesAsync();
                await ReceptionistTestWindows.SeedAsync(db, connection.Id, version.Id, changed, Clock.Now);
                binding = (await Admission.GetIngressBindingAsync("openai", "synthetic-ingress"))!;
            }
            var admission = await Admission.ReserveAsync(new("openai", "synthetic-ingress", "synthetic-project", session, "synthetic-event:" + session, controlledTest,
                binding.MarketingConnectionId, binding.ConfigurationId, binding.Owner.Key));
            Assert.Equal("reserved", admission.Reason);
            Assert.NotNull(admission.CallId);
            var accept = await Admission.ClaimProviderAcceptAsync(admission.CallId.Value, "synthetic-accept-worker");
            Assert.True(accept.MayExecute);
            await Admission.RecordProviderStateAsync(admission.CallId!.Value, "accepted", "synthetic-provider-accepted");
            return (await Admission.GetCallContextAsync(admission.CallId.Value))!;
        }

        internal async Task<Guid> ClaimAsync(ReceptionistCallContext call, ConfirmedReceptionistInquiry facts)
        {
            var lease = (await Admission.AcquireSidebandLeaseAsync(call.CallId, "synthetic-worker", TimeSpan.FromSeconds(30)))!;
            Leases[call.CallId] = lease.Token;
            var claim = await Admission.ClaimActionAsync(call.CallId, "confirmed-inquiry", ReceptionistCapabilities.CanonicalIntake,
                ReceptionistInquiryAuthority.CanonicalArgumentsSha256(facts), Receipt, lease.Token);
            Assert.True(claim.MayExecute); return claim.ActionId!.Value;
        }

        public ValueTask DisposeAsync() { File.Delete(path); return ValueTask.CompletedTask; }
    }

    private sealed class LostCommitAcknowledgement(int commitsToSkip = 0) : DbTransactionInterceptor
    {
        private int commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == commitsToSkip + 1) throw new IOException("Synthetic lost response after the database committed.");
            return Task.CompletedTask;
        }
    }

    private sealed class InquirySchemaContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.ApplyConfiguration(new MarketingConnectionConfiguration());
            model.ApplyConfiguration(new CrmHistoryEntryConfiguration());
            ReceptionistModelConfiguration.Configure(model, Database.ProviderName);
            model.Entity<AgentTrackingProfile>().Ignore(x => x.Aliases);
            model.Entity<AgentProfile>(); model.Entity<CommerceBusinessStorefrontSettings>();
            model.Entity<WebsiteLead>(e =>
            {
                e.ToTable("WebsiteLeads"); e.HasKey(x => x.Id); e.HasIndex(x => x.LeadId).IsUnique();
                e.Property(x => x.FirstName).IsRequired().HasMaxLength(120); e.Property(x => x.Email).IsRequired().HasMaxLength(320);
                e.HasOne<CommerceBusiness>().WithMany().HasForeignKey(x => x.CommerceBusinessId).OnDelete(DeleteBehavior.Restrict);
            });
            model.Entity<WorkstationLeadProfile>(e =>
            {
                e.ToTable("WorkstationLeadProfiles"); e.HasKey(x => x.LeadId);
                e.Property(x => x.RowVersion).IsRequired().IsConcurrencyToken().HasDefaultValueSql("X''");
                e.HasOne<CommerceBusiness>().WithMany().HasForeignKey(x => x.CommerceBusinessId).OnDelete(DeleteBehavior.Restrict);
            });
            model.Entity<WebsiteLeadIntakeLink>(e =>
            {
                e.ToTable("WebsiteLeadIntakeLinks"); e.HasKey(x => x.Id); e.HasIndex(x => x.WebsiteLeadRowId).IsUnique();
                e.HasOne<WebsiteLead>().WithMany().HasForeignKey(x => x.WebsiteLeadRowId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<WorkstationLeadProfile>().WithMany().HasForeignKey(x => x.WorkstationLeadId).HasPrincipalKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
            });
            model.Entity<CommerceWebsiteInquiry>();
            model.Entity<LeadAppointment>();
            model.Entity<ClientProfile>(); model.Entity<AgentClient>(); model.Entity<CommerceBusinessMember>(); model.Entity<AccountLifecycleRecord>();
            var allowed = new HashSet<Type>(typeof(ReceptionistCall).Assembly.GetTypes().Where(x => x.Namespace == "Domain.Entities" && x.Name.StartsWith("Receptionist", StringComparison.Ordinal)))
            { typeof(CrmHistoryEntry), typeof(MarketingConnection), typeof(AgentTrackingProfile), typeof(AgentProfile), typeof(CommerceBusiness), typeof(CommerceBusinessStorefrontSettings),
                typeof(WebsiteLead), typeof(WebsiteLeadIntakeLink), typeof(WorkstationLeadProfile), typeof(CommerceWebsiteInquiry), typeof(LeadAppointment),
                typeof(ClientProfile), typeof(AgentClient), typeof(CommerceBusinessMember), typeof(AccountLifecycleRecord) };
            foreach (var entity in model.Model.GetEntityTypes().ToArray())
                if (!allowed.Contains(entity.ClrType)) model.Ignore(entity.ClrType);
        }
    }
}
