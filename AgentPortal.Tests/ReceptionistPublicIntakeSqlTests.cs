using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Leads;
using Infrastructure.Reception;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ReceptionistPublicIntakeSqlTests
{
    [PersistentAssistantFact]
    public async Task PublicRoleSavesWithoutEmployeeSessionOrNotificationRecipientAndReplaysOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("MASTERAPP_LOCAL_DB_CONNECTION")!;
        var target = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal("127.0.0.1,14339", target.DataSource); Assert.Equal("MasterAppLocalTest", target.InitialCatalog);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(100),value) FROM sys.extended_properties WHERE class=0 AND name=N'LegendLocalTestIdentity'";
            Assert.Equal(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY"), await command.ExecuteScalarAsync());
        }
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new MasterAppDbContext(options);
        var unique = Guid.NewGuid().ToString("N");
        var business = new CommerceBusiness { Key = "synthetic-" + unique, DisplayName = "Synthetic public intake", OwnerEmail = "", IsActive = true, Status = "Active" };
        var owner = MarketingOwnerScope.Business(business.Id);
        var account = new ReceptionistBudgetAccount { Kind = "owner", ScopeKey = owner.Key, Currency = "USD", TimeZoneId = "UTC" };
        var period = new ReceptionistBudgetPeriod { BudgetAccountId = account.Id, MonthKey = 202611, StartUtc = DateTime.UtcNow.AddDays(-1), EndUtc = DateTime.UtcNow.AddDays(1), OpeningReceipt = "synthetic", ReconciledThroughUtc = DateTime.UtcNow };
        var connectionRow = new MarketingConnection { OwnerKey = owner.Key, OwnerType = owner.OwnerType, CommerceBusinessId = business.Id, Provider = "openai" };
        var configuration = new ReceptionistBusinessConfiguration
        {
            BusinessName = "Synthetic public intake", ConversationInstructions = "Use confirmed synthetic facts only.", BusinessPolicy = "No private account access.",
            TimeZoneId = "UTC", Currency = "USD", MonthlyCashLimit = 25, MonthlyConsumptionLimit = 25, MonthlyVoiceSecondsLimit = 1000,
            MaximumConcurrentCalls = 1, MaximumCallSeconds = 60, AdmissionWindowSeconds = 60, MaximumCallsPerWindow = 2,
            Hours = Enum.GetValues<DayOfWeek>().Select(day => new ReceptionistHoursWindow(day, TimeOnly.MinValue, new TimeOnly(23,59,59))).ToArray(),
            RequestedCapabilities = [ReceptionistCapabilities.CanonicalIntake],
            IntakeRulesJson = """{"canonicalInquiry":{"services":[{"serviceKey":"website_services","crmBucket":"ServiceInquiry"}],"notifyOwner":true}}"""
        };
        var binding = new ReceptionistProviderBinding
        {
            IngressKey = "synthetic-" + unique, ProviderProjectKey = "synthetic-" + unique, Model = "synthetic-model", Voice = "synthetic-voice", BackendMode = "application_services", Transport = "sip",
            CredentialReference = "synthetic-no-credential", WebhookSecretReference = "synthetic-no-secret", ProviderVerificationReceipt = "synthetic-provider", ExclusiveProjectVerificationReceipt = "synthetic-exclusive",
            CostVerificationReceipt = "synthetic-cost", DisclosureConfigurationReceipt = "synthetic-disclosure", RetentionConfigurationReceipt = "synthetic-retention", CarrierFallbackReceipt = "synthetic-fallback",
            MaximumSupportedCallSeconds = 60, ControlledTestOnly = false, PublicActivationReceipt = "synthetic-local-only",
            Costs = [new("provider_cost", Guid.NewGuid(), 1m, 0m, 1m, 0m)],
            Capabilities = [new(ReceptionistCapabilities.CanonicalIntake, ReceptionistInquiryAuthority.RouteKey, ReceptionistCapabilityStates.Accepted, "synthetic-acceptance")]
        };
        var json = ReceptionistConfigurationCodec.Serialize(configuration);
        var version = new ReceptionistConfigurationVersion { MarketingConnectionId = connectionRow.Id, Version = 1, ConfigurationJson = json,
            ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(json), AcceptedBindingJson = JsonSerializer.Serialize(binding, ReceptionistConfigurationCodec.Json), CreatedByActorId = "synthetic", CreatedUtc = DateTime.UtcNow };
        var call = new ReceptionistCall { MarketingConnectionId = connectionRow.Id, ConfigurationVersionId = version.Id,
            OwnerBudgetAccountId = account.Id, OwnerBudgetPeriodId = period.Id, Provider = "openai", IngressKey = binding.IngressKey,
            ProviderProjectKey = binding.ProviderProjectKey, ProviderSessionId = "synthetic-" + unique, State = "accepted", ReservedVoiceSeconds = 60, ReservedUtc = DateTime.UtcNow, MustStopUtc = DateTime.UtcNow.AddMinutes(1) };
        var facts = new ConfirmedReceptionistInquiry { AcquisitionClass = "observed_genuine_inquiry_pending_reconciliation", ServiceKey = "website_services",
            RequestSummary = "Synthetic inquiry persistence regression", FirstName = "Synthetic", LastName = "Caller", Email = "synthetic@local-test.invalid",
            ContactStatus = "caller_confirmed_for_requested_purpose", RequestedContactMethod = "email_requested", ContactPermissionState = "caller_requested_purpose_only",
            ContactPurpose = "Discuss the synthetic inquiry" };
        var action = new ReceptionistActionIntent { CallId = call.Id, LogicalActionKey = "synthetic-confirmed-inquiry", CapabilityKey = ReceptionistCapabilities.CanonicalIntake,
            RouteKey = ReceptionistInquiryAuthority.RouteKey, ArgumentsSha256 = ReceptionistInquiryAuthority.CanonicalArgumentsSha256(facts), ConfirmationReceipt = "synthetic-confirmed",
            ExecutionLeaseToken = Guid.NewGuid(), ExecutionFence = 1, WorkerId = "synthetic-service-worker", State = "pending", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        try
        {
            db.AddRange(business, new CommerceBusinessStorefrontSettings { CommerceBusinessId = business.Id }, account, period, connectionRow, version, call, action); await db.SaveChangesAsync();
            // No HttpContext, ClaimsPrincipal, employee cookie or provider credentials.
            var settings = new ConfigurationBuilder().Build();
            var admission = new ReceptionistAdmissionService(options, TimeProvider.System);
            var context = (await admission.GetCallContextAsync(call.Id))!;
            var authority = new ReceptionistInquiryAuthority(options, admission, settings, NullLogger<WebsiteLifeLeadCaptureService>.Instance, TimeProvider.System);
            Assert.Null(await new WebsiteIntakeRecipientResolver(db, settings).ResolveAsync(owner));
            var first = await authority.PersistConfirmedAsync(context, action.Id, facts, "synthetic-confirmed");
            Assert.True(first.Created);
            var verified = (await authority.ReadBackAsync(owner, call.Id, action.Id))!;
            Assert.NotNull(verified); Assert.Equal("owner_notification_pending", verified.DeliveryState);
            Assert.False((await authority.PersistConfirmedAsync(context, action.Id, facts, "synthetic-confirmed")).Created);
            Assert.Equal(1, await db.WebsiteLeads.CountAsync(x => x.CommerceBusinessId == business.Id));
            Assert.Equal(1, await db.WorkstationLeadProfiles.CountAsync(x => x.CommerceBusinessId == business.Id));
            business.OwnerEmail = "synthetic-owner@local-test.invalid"; await db.SaveChangesAsync();
            var recipient = await new WebsiteIntakeRecipientResolver(db, settings).ResolveAsync(owner);
            var lead = await db.WebsiteLeads.SingleAsync(x => x.CommerceBusinessId == business.Id);
            var sends = 0;
            Task<bool> FakeSend(System.Threading.CancellationToken _) { sends++; return Task.FromResult(true); }
            await WebsiteLeadNotificationAuthority.DeliverAsync(db, lead, recipient!, FakeSend);
            await WebsiteLeadNotificationAuthority.DeliverAsync(db, lead, recipient!, FakeSend);
            Assert.Equal(1, sends);
            Assert.NotNull((await authority.ReadBackAsync(owner, call.Id, action.Id)));
        }
        finally
        {
            await using var cleanup = new MasterAppDbContext(options);
            await cleanup.WebsiteLeadIntakeLinks.Where(x => x.CommerceBusinessId == business.Id).ExecuteDeleteAsync();
            await cleanup.WorkstationLeadProfiles.Where(x => x.CommerceBusinessId == business.Id).ExecuteDeleteAsync();
            await cleanup.WebsiteLeads.Where(x => x.CommerceBusinessId == business.Id).ExecuteDeleteAsync();
            await cleanup.Set<ReceptionistActionIntent>().Where(x => x.Id == action.Id).ExecuteDeleteAsync();
            await cleanup.Set<ReceptionistCall>().Where(x => x.Id == call.Id).ExecuteDeleteAsync();
            await cleanup.Set<ReceptionistConfigurationVersion>().Where(x => x.Id == version.Id).ExecuteDeleteAsync();
            await cleanup.MarketingConnections.Where(x => x.Id == connectionRow.Id).ExecuteDeleteAsync();
            await cleanup.Set<ReceptionistBudgetPeriod>().Where(x => x.Id == period.Id).ExecuteDeleteAsync();
            await cleanup.Set<ReceptionistBudgetAccount>().Where(x => x.Id == account.Id).ExecuteDeleteAsync();
            await cleanup.CommerceBusinessStorefrontSettings.Where(x => x.CommerceBusinessId == business.Id).ExecuteDeleteAsync();
            await cleanup.CommerceBusinesses.Where(x => x.Id == business.Id).ExecuteDeleteAsync();
        }
    }
}
