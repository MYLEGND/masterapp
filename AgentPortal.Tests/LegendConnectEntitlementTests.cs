using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Domain.Billing;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.Moderation;
using Infrastructure.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectEntitlementTests
{
    private static readonly MessagingActor Account = new("member-1", MessagingParticipantTypes.Client);

    [Fact]
    public async Task SameLanguage_BypassesProviderAndKeepsQuotaAtZero()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new TranslationAccessStub(granted: true);
        var authority = Authority(db, access, allowance: 5);
        var provider = new RecordingProvider();
        var router = Router(db, provider, authority, allowance: 5);

        var result = await router.TranslateForAccountAsync("hello", "en", "en", Account, Reference("same"));

        Assert.True(result.Succeeded);
        Assert.Equal(0, provider.TranslateCalls);
        var usage = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(0, usage.ConsumedCharacters);
        Assert.Equal(0, usage.ReservedCharacters);
        Assert.Equal(5, usage.SameLanguageCharactersAvoided);
    }

    [Fact]
    public async Task ExactAllowance_IsConsumedAndNextBillableRequestCannotReachProvider()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new TranslationAccessStub(granted: true);
        var authority = Authority(db, access, allowance: 5);
        var provider = new RecordingProvider();
        var router = Router(db, provider, authority, allowance: 5);

        var exact = await router.TranslateForAccountAsync("hello", "ht", "en", Account, Reference("exact"));
        var over = await router.TranslateForAccountAsync("!", "ht", "en", Account, Reference("over"));

        Assert.True(exact.Succeeded);
        Assert.False(over.Succeeded);
        Assert.Equal("translation_quota_exhausted", over.ErrorCode);
        Assert.Equal(1, provider.TranslateCalls);
        var usage = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(5, usage.ConsumedCharacters);
        Assert.Equal(0, usage.ReservedCharacters);
        Assert.Equal(1, usage.QuotaDeniedRequestCount);
    }

    [Fact]
    public async Task ConcurrentReservations_CannotOverspendOneFiniteAccount()
    {
        var databaseName = "legend-connect-entitlement-" + Guid.NewGuid().ToString("N");
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared;Default Timeout=30";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var setup = new MasterAppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        await using var firstDb = new MasterAppDbContext(options);
        await using var secondDb = new MasterAppDbContext(options);
        var access = new TranslationAccessStub(granted: true);
        var firstAuthority = Authority(firstDb, access, allowance: 6);
        var secondAuthority = Authority(secondDb, access, allowance: 6);

        var first = firstAuthority.TryReserveAsync(new TranslationQuotaReservationRequest(
            Account, Reference("concurrent-one"), "en", "ht", "AzureTranslator", 4));
        var second = secondAuthority.TryReserveAsync(new TranslationQuotaReservationRequest(
            Account, Reference("concurrent-two"), "en", "ht", "AzureTranslator", 4));
        var results = await Task.WhenAll(first, second);

        Assert.Single(results.Where(result => result.Succeeded));
        Assert.Single(results.Where(result => !result.Succeeded && result.ErrorCode == "translation_quota_exhausted"));
        await using var verification = new MasterAppDbContext(options);
        var usage = await verification.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(4, usage.ReservedCharacters);
        Assert.Equal(0, usage.ConsumedCharacters);
    }

    [Fact]
    public async Task UnlimitedAccount_IsStillMetered()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new TranslationAccessStub(granted: true);
        var authority = Authority(db, access, allowance: 0);
        db.LegendTranslationEntitlements.Add(new LegendTranslationEntitlement
        {
            Id = Guid.NewGuid(), UserId = Account.UserId, ParticipantType = Account.ParticipantType,
            IsUnlimited = true, MonthlyCharacterAllowance = 0, EntitlementSource = "FounderUnlimited"
        });
        await db.SaveChangesAsync();
        var provider = new RecordingProvider();
        var router = Router(db, provider, authority, allowance: 0);

        var result = await router.TranslateForAccountAsync("unlimited", "ht", "en", Account, Reference("unlimited"));

        Assert.True(result.Succeeded);
        var snapshot = await authority.GetSnapshotAsync(Account);
        Assert.True(snapshot.IsUnlimited);
        Assert.Null(snapshot.RemainingCharacters);
        Assert.Equal(9, snapshot.ConsumedCharacters);
        Assert.Equal(1, (await db.LegendTranslationUsagePeriods.SingleAsync()).ProviderOperationCount);
    }

    [Fact]
    public async Task ProviderFailure_ReleasesReservationAndPreservesQuota()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new TranslationAccessStub(granted: true);
        var authority = Authority(db, access, allowance: 20);
        var provider = new RecordingProvider(succeed: false);
        var router = Router(db, provider, authority, allowance: 20);

        var result = await router.TranslateForAccountAsync("failure", "ht", "en", Account, Reference("provider-failure"));

        Assert.False(result.Succeeded);
        var usage = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(0, usage.ConsumedCharacters);
        Assert.Equal(0, usage.ReservedCharacters);
        Assert.Equal(1, usage.ProviderOperationCount);
        Assert.Equal(1, usage.ProviderFailureCount);
        var ledger = await db.LegendTranslationUsageLedgers.SingleAsync();
        Assert.False(ledger.Succeeded);
        Assert.Equal("ProviderFailed", ledger.State);
    }

    [Fact]
    public async Task DurableRequestReference_PreventsDuplicateProviderChargeOnRetry()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new TranslationAccessStub(granted: true);
        var authority = Authority(db, access, allowance: 20);
        var provider = new RecordingProvider();
        var router = Router(db, provider, authority, allowance: 20);
        var reference = Reference("retry");

        var first = await router.TranslateForAccountAsync("retry", "ht", "en", Account, reference);
        var retry = await router.TranslateForAccountAsync("retry", "ht", "en", Account, reference);

        Assert.True(first.Succeeded);
        Assert.False(retry.Succeeded);
        Assert.Equal("translation_result_already_exists", retry.ErrorCode);
        Assert.Equal(1, provider.TranslateCalls);
        Assert.Equal(5, (await db.LegendTranslationUsagePeriods.SingleAsync()).ConsumedCharacters);
        Assert.Single(await db.LegendTranslationUsageLedgers.ToListAsync());
    }

    [Fact]
    public async Task NewCalendarPeriod_LeavesHistoricalConsumptionAuditableAndStartsFresh()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var priorDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-1);
        var previous = new DateOnly(priorDate.Year, priorDate.Month, 1);
        db.LegendTranslationUsagePeriods.Add(new LegendTranslationUsagePeriod
        {
            Id = Guid.NewGuid(), UserId = Account.UserId, ParticipantType = Account.ParticipantType,
            PeriodStart = previous, ConsumedCharacters = 123, ProviderBillableCharacters = 123,
            UpdatedUtc = DateTime.UtcNow.AddMonths(-1)
        });
        await db.SaveChangesAsync();
        var authority = Authority(db, new TranslationAccessStub(granted: true), allowance: 500);

        var current = await authority.GetSnapshotAsync(Account);

        Assert.Equal(0, current.ConsumedCharacters);
        Assert.Equal(500, current.RemainingCharacters);
        Assert.Equal(DateTime.UtcNow.Month, current.PeriodStartUtc.Month);
        Assert.Equal(123, (await db.LegendTranslationUsagePeriods.SingleAsync(item => item.PeriodStart == previous)).ConsumedCharacters);
    }

    [Fact]
    public async Task RevokedAccess_BlocksNewProviderTranslationWithoutBlockingTheCaller()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var access = new TranslationAccessStub(granted: false);
        var authority = Authority(db, access, allowance: 100);
        var provider = new RecordingProvider();
        var router = Router(db, provider, authority, allowance: 100);

        var result = await router.TranslateForAccountAsync("original remains", "ht", "en", Account, Reference("revoked"));

        Assert.False(result.Succeeded);
        Assert.Equal("translation_access_revoked", result.ErrorCode);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Empty(await db.LegendTranslationUsageLedgers.ToListAsync());
    }

    [Fact]
    public async Task TrustedMemory_PrecedesQuotaAndCapacity_AndSeparatesFallbackRequiredFromProviderCalls()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration(allowance: 0);
        var access = new TranslationAccessStub(granted: true);
        var authority = new TranslationEntitlementAuthority(
            db,
            access,
            configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var retained = await corpus.SubmitApprovedKnowledgeAsync(new LegendConnectKnowledgeSubmission(
            "en", "Hi, how are you doing today?", "ht", "Bonjour, koman ou ye jodi a?",
            "Everyday conversation", "Plans", null, "FounderApproved"));
        Assert.True(retained.Succeeded);

        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            entitlements: authority);

        var memory = await router.TranslateForAccountAsync(
            "  Hi, how are you doing today?  ", "ht", "en-US", Account, Reference("known-memory"));
        var quotaDenied = await router.TranslateForAccountAsync(
            "This phrase has no retained translation.", "ht", "en", Account, Reference("missing-memory"));

        Assert.True(memory.Succeeded);
        Assert.Equal("Bonjour, koman ou ye jodi a?", memory.TranslatedText);
        Assert.Equal("LegendConnectTranslationMemory", memory.Provider);
        Assert.False(quotaDenied.Succeeded);
        Assert.Equal("translation_quota_exhausted", quotaDenied.ErrorCode);
        Assert.Equal(0, provider.TranslateCalls);
        var deniedLedger = Assert.Single(await db.LegendTranslationUsageLedgers.ToListAsync());
        Assert.False(deniedLedger.ProviderExecuted);
        Assert.False(deniedLedger.Succeeded);
        Assert.Equal("QuotaDenied", deniedLedger.State);

        var pair = await db.LegendTranslationPairDemands.SingleAsync(item => item.PairKey == "en:ht");
        Assert.Equal(2, pair.TranslationRequestCount);
        Assert.Equal(1, pair.TranslationMemoryHitCount);
        Assert.Equal(1, pair.AzureFallbackCount);
        var usage = await db.LegendTranslationSystemUsages.SingleAsync();
        Assert.Equal(0, usage.ProviderOperationCount);
        Assert.Equal(0, usage.ProviderBillableCharacters);
        Assert.Equal(1, usage.QuotaDeniedRequestCount);
        Assert.True(usage.TranslationMemoryCharactersAvoided > 0);
    }

    [Fact]
    public async Task EntitlementMutation_RequiresFounderAuthorityAndDoesNotContainMessageBodies()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var client = Client(Account.UserId, "Entitlement");
        db.ClientProfiles.Add(client);
        db.ClientSubscriptions.Add(new ClientSubscription
        {
            Id = Guid.NewGuid(),
            ClientProfileId = client.Id,
            AcceptedOfferId = Guid.NewGuid(),
            OwnerAgentUserId = "founder",
            Status = ClientSubscriptionStatus.Active,
            PaymentStanding = ClientSubscriptionPaymentStanding.Current,
            MonthlyAmountCents = 1,
            Currency = "USD"
        });
        await db.SaveChangesAsync();
        var denied = Authority(db, new TranslationAccessStub(granted: true, founder: false), allowance: 100);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.SetEntitlementAsync(
            "founder", new TranslationEntitlementMutation(Account, 250, false, "FounderCustom", true)));

        var allowed = Authority(db, new TranslationAccessStub(granted: true, founder: true), allowance: 100);
        var snapshot = await allowed.SetEntitlementAsync(
            "founder", new TranslationEntitlementMutation(Account, 250, false, "FounderCustom", true));

        Assert.Equal(250, snapshot.CharacterAllowance);
        Assert.True(snapshot.IsFounderOverride);
        Assert.DoesNotContain(typeof(LegendTranslationUsageLedger).GetProperties(), property =>
            property.Name.Contains("body", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FounderAccountSearch_UsesOnlyActiveCurrentPayingClientCrmProfilesAndBoundsPreviews()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        for (var index = 1; index <= 9; index++)
        {
            var client = Client($"paid-client-{index}", index == 9 ? "Marisol" : $"Preview {index}");
            client.CrmStatus = "Active";
            db.ClientProfiles.Add(client);
            db.ClientSubscriptions.Add(CurrentSubscription(client));
        }

        var closed = Client("lead-closed-account", "Closed");
        closed.CrmStatus = "Active";
        var noSubscription = Client("lead-no-subscription", "No Subscription");
        noSubscription.CrmStatus = "Active";
        var inactiveCrm = Client("lead-inactive-crm", "Inactive");
        inactiveCrm.CrmStatus = "Lead";
        db.ClientProfiles.AddRange(closed, noSubscription, inactiveCrm);
        db.ClientSubscriptions.Add(CurrentSubscription(closed));
        db.ClientSubscriptions.Add(CurrentSubscription(inactiveCrm));
        db.AccountLifecycleRecords.Add(new AccountLifecycleRecord
        {
            Id = Guid.NewGuid(),
            ProfileId = closed.Id,
            UserId = closed.ClientUserId,
            ParticipantType = MessagingParticipantTypes.Client,
            State = Domain.Accounts.AccountLifecycleStates.Closed,
            ClosedUtc = DateTime.UtcNow
        });
        db.ControlledResourceGrants.Add(new ControlledResourceGrant
        {
            UserId = closed.ClientUserId,
            ParticipantType = MessagingParticipantTypes.Client,
            ResourceType = ControlledResourceTypes.LanguageTranslation,
            IsActive = true,
            GrantedUtc = DateTime.UtcNow,
            GrantedByUserId = "founder"
        });
        db.LegendTranslationEntitlements.Add(new LegendTranslationEntitlement
        {
            Id = Guid.NewGuid(),
            UserId = closed.ClientUserId,
            ParticipantType = MessagingParticipantTypes.Client,
            MonthlyCharacterAllowance = 5,
            EntitlementSource = "FounderManaged"
        });
        await db.SaveChangesAsync();

        var authority = Authority(db, new TranslationAccessStub(granted: true, founder: true), allowance: 100);

        var preview = await authority.SearchFounderAccountsAsync(null, 50);
        var search = await authority.SearchFounderAccountsAsync("mar", 8);

        Assert.Equal(8, preview.Accounts.Count);
        Assert.True(preview.HasMore);
        Assert.DoesNotContain(preview.Accounts, account => account.Account.UserId.StartsWith("lead-", StringComparison.Ordinal));
        Assert.Single(search.Accounts);
        Assert.Equal("paid-client-9", search.Accounts[0].Account.UserId);
        Assert.False(search.HasMore);
        Assert.True(await authority.IsFounderEntitlementEligibleAsync(new MessagingActor("paid-client-1", MessagingParticipantTypes.Client)));
        Assert.False(await authority.IsFounderEntitlementEligibleAsync(new MessagingActor(closed.ClientUserId, MessagingParticipantTypes.Client)));
        Assert.False(await authority.IsFounderEntitlementEligibleAsync(new MessagingActor(noSubscription.ClientUserId, MessagingParticipantTypes.Client)));
    }

    [Fact]
    public async Task GroupMessage_DetectsActualSourceAndTranslatesOncePerUniqueTargetLanguage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var agent = new AgentProfile
        {
            Id = Guid.NewGuid(), AgentUserId = "group-agent", AgentUpn = "group-agent@example.test",
            FullName = "Group Agent", IsActive = true
        };
        var clients = new[]
        {
            Client("group-client-1", "One"),
            Client("group-client-2", "Two"),
            Client("group-client-3", "Three")
        };
        db.Add(agent);
        db.ClientProfiles.AddRange(clients);
        foreach (var client in clients)
        {
            db.AgentClients.Add(new AgentClient
            {
                AgentUserId = agent.AgentUserId,
                AgentUpn = agent.AgentUpn,
                ClientUserId = client.ClientUserId
            });
            db.ClientEntitlements.Add(new ClientEntitlement
            {
                Id = Guid.NewGuid(), ClientProfileId = client.Id,
                EntitlementKey = BillingEntitlementKeys.ClientAppFullAccess,
                Status = ClientEntitlementStatus.Active,
                SourceType = ClientEntitlementSourceType.Subscription,
                SourceId = "group-translation-test",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
            db.ControlledResourceGrants.Add(new ControlledResourceGrant
            {
                UserId = client.ClientUserId,
                ParticipantType = MessagingParticipantTypes.Client,
                ResourceType = ControlledResourceTypes.LanguageTranslation,
                IsActive = true,
                GrantedUtc = DateTime.UtcNow,
                GrantedByUserId = "founder"
            });
        }
        db.MobileProfileSettings.AddRange(
            new MobileProfileSettings { ProfileId = clients[0].Id, ParticipantType = MessagingParticipantTypes.Client, PreferredCommunicationLanguage = "ht" },
            new MobileProfileSettings { ProfileId = clients[1].Id, ParticipantType = MessagingParticipantTypes.Client, PreferredCommunicationLanguage = "ht" },
            new MobileProfileSettings { ProfileId = clients[2].Id, ParticipantType = MessagingParticipantTypes.Client, PreferredCommunicationLanguage = "fr" });
        await db.SaveChangesAsync();

        var translator = new GroupCountingTranslationService();
        var images = new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance);
        var service = new MessagingService(
            db,
            NullLogger<MessagingService>.Instance,
            new CommunityTextModerationService(new ConfigurationBuilder().Build()),
            images,
            new ControlledResourceAccessService(db),
            translator,
            new NotificationEngine(
                db,
                images,
                new NoopNotificationRealtimePublisher(),
                new ApplePushDeliverySignal(),
                NullLogger<NotificationEngine>.Instance));

        var created = await service.CreateGroupAsync(new CreateMessagingGroupCommand(
            new MessagingActor(agent.AgentUserId, MessagingParticipantTypes.Agent),
            clients.Select(client => new MessagingParticipantReference(client.ClientUserId, MessagingParticipantTypes.Client)).ToArray(),
            "Language group",
            "actual English content"));

        Assert.True(created.Succeeded, created.ErrorMessage);
        Assert.Equal(1, translator.DetectionCalls);
        Assert.Equal("en", translator.LastDetectedLanguage);
        Assert.Equal(2, translator.AccountTranslationCalls);
        Assert.Equal(1, translator.TargetCounts["ht"]);
        Assert.Equal(1, translator.TargetCounts["fr"]);
        Assert.Equal(2, await db.MessageTranslations.CountAsync());
    }

    private static TranslationEntitlementAuthority Authority(
        MasterAppDbContext db,
        IControlledResourceAccessService access,
        long allowance) => new(
        db,
        access,
        Configuration(allowance),
        NullLogger<TranslationEntitlementAuthority>.Instance);

    private static LegendConnectTranslationRouter Router(
        MasterAppDbContext db,
        ITranslationProvider provider,
        ITranslationEntitlementAuthority authority,
        long allowance)
    {
        var configuration = Configuration(allowance);
        return new LegendConnectTranslationRouter(
            provider,
            new LegendLanguageRegistry(db, configuration),
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            entitlements: authority);
    }

    private static IConfiguration Configuration(long allowance) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Entitlements:DefaultMonthlyCharacterAllowance"] = allowance.ToString(),
            ["LegendConnect:Entitlements:ReservationLeaseSeconds"] = "45",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
        })
        .Build();

    private static string Reference(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class TranslationAccessStub : IControlledResourceAccessService
    {
        private readonly bool _granted;
        private readonly bool _founder;

        public TranslationAccessStub(bool granted, bool founder = false)
        {
            _granted = granted;
            _founder = founder;
        }

        public Task<ControlledResourceAccess> GetAccessAsync(
            MessagingActor actor,
            string resourceType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(
                resourceType,
                _granted ? ControlledResourceAccessStates.Granted : ControlledResourceAccessStates.NotGranted,
                _founder));

        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(_founder);

        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(_founder);

        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        private readonly bool _succeed;

        public RecordingProvider(bool succeed = true) => _succeed = succeed;

        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(_succeed
                ? new TranslationProviderResult(true, "translated", sourceLanguage, ProviderName)
                : new TranslationProviderResult(false, null, sourceLanguage, ProviderName, "translation_provider_failed"));
        }
    }

    private static ClientProfile Client(string userId, string suffix) => new()
    {
        Id = Guid.NewGuid(), ClientUserId = userId, ExternalIdentityObjectId = userId,
        FirstName = "Group", LastName = suffix, Email = $"{userId}@example.test",
        CrmNotes = "{\"recordType\":\"Client\",\"pipelineStage\":\"Client\"}"
    };

    private static ClientSubscription CurrentSubscription(ClientProfile client) => new()
    {
        Id = Guid.NewGuid(),
        ClientProfileId = client.Id,
        AcceptedOfferId = Guid.NewGuid(),
        OwnerAgentUserId = "founder",
        Status = ClientSubscriptionStatus.Active,
        PaymentStanding = ClientSubscriptionPaymentStanding.Current,
        MonthlyAmountCents = 1,
        Currency = "USD"
    };

    private sealed class GroupCountingTranslationService : IAccountScopedTranslationService
    {
        public int DetectionCalls { get; private set; }
        public int AccountTranslationCalls { get; private set; }
        public string? LastDetectedLanguage { get; private set; }
        public Dictionary<string, int> TargetCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        {
            DetectionCalls++;
            LastDetectedLanguage = "en";
            return Task.FromResult(new TranslationDetectionResult(true, LastDetectedLanguage));
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationProviderResult(true, $"{targetLanguage}:{text}", sourceLanguage, "TestTranslator"));

        public Task<TranslationProviderResult> TranslateForAccountAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage,
            MessagingActor account,
            string requestReference,
            CancellationToken cancellationToken = default)
        {
            AccountTranslationCalls++;
            TargetCounts[targetLanguage] = TargetCounts.TryGetValue(targetLanguage, out var count) ? count + 1 : 1;
            return Task.FromResult(new TranslationProviderResult(true, $"{targetLanguage}:{text}", sourceLanguage, "TestTranslator"));
        }
    }

    private sealed class NoopNotificationRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
