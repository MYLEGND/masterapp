using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

/// <summary>
/// Canonical provenance labels for evidence that moves through the shared
/// language-intelligence pipeline. Founder approval belongs to the source
/// asset; a provider result remains provider-derived until a Founder verifies
/// that exact directional alignment.
/// </summary>
internal static class LegendConnectKnowledgeProvenance
{
    internal const string FounderApproved = "FounderApproved";
    internal const string ProviderDerived = "ProviderDerived";
    internal const string ConsentedLiveTranslation = "ConsentedLiveTranslation";

    // Machine-derived material may receive this provenance only after the
    // existing canonical LEGEND evidence authorities independently validate
    // the exact Phase-3 proposal and evidence lineage. It never impersonates
    // FounderApproved or HumanVerified evidence.
    internal const string SystemValidatedMachine = "SystemValidatedMachine";
}

internal sealed class NullTranslationLearningPublisher : ITranslationLearningPublisher
{
    internal static readonly NullTranslationLearningPublisher Instance = new();
    private NullTranslationLearningPublisher() { }

    public Task TryPublishAsync(TranslationLearningCandidate candidate, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Central privacy gate for the learning pipeline. Messaging is private by
/// default. A successful translation becomes retainable only when every
/// participant present when the message was sent has explicitly opted in via
/// the one MobileProfileSettings authority. Ambiguity fails closed.
/// </summary>
internal sealed class LegendTranslationTrainingEligibilityPolicy
{
    private readonly MasterAppDbContext _db;

    public LegendTranslationTrainingEligibilityPolicy(MasterAppDbContext db) => _db = db;

    public async Task<TranslationLearningEligibility> EvaluateAsync(
        TranslationLearningCandidate candidate,
        string normalizedTargetLanguage,
        CancellationToken cancellationToken)
    {
        if (candidate.SourceMessageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(candidate.SourceText) ||
            string.IsNullOrWhiteSpace(candidate.TargetText))
        {
            return Ineligible("IneligibleInvalidTranslation");
        }

        var message = await _db.InternalMessages
            .AsNoTracking()
            .Where(item => item.Id == candidate.SourceMessageId)
            .Select(item => new MessageEligibilityRow(
                item.ConversationId,
                item.SentUtc,
                item.IsDeleted,
                item.VerificationReviewRequestId.HasValue))
            .SingleOrDefaultAsync(cancellationToken);
        // Preserve the existing privacy-safe classification for an absent or
        // deleted source. An operational caller should always have persisted
        // the message first; either way, no body is eligible for retention.
        if (message is null || message.IsDeleted)
            return Ineligible("IneligiblePrivateMessage");

        // A learning candidate is only a post-persistence derivative of a
        // completed operational translation. Keep that boundary in the
        // server-owned policy rather than trusting every caller to uphold it.
        // The existing unique message/target cache is the canonical proof that
        // this exact delivered result succeeded; failed/empty provider output
        // never creates this row in MessagingService.
        var persistedTranslation = await _db.MessageTranslations
            .AsNoTracking()
            .Where(item => item.InternalMessageId == candidate.SourceMessageId &&
                item.TargetLanguage == normalizedTargetLanguage)
            .Select(item => new PersistedTranslationRow(item.TranslatedText, item.Provider))
            .SingleOrDefaultAsync(cancellationToken);
        if (persistedTranslation is null ||
            string.IsNullOrWhiteSpace(persistedTranslation.Provider) ||
            !string.Equals(
                persistedTranslation.TranslatedText.Trim(),
                candidate.TargetText.Trim(),
                StringComparison.Ordinal))
        {
            return Ineligible("IneligibleUnpersistedTranslation");
        }

        // Verification-review conversations are system-protected rather than
        // ordinary member conversation material and can never enter learning.
        if (message.IsProtected)
            return Ineligible("IneligibleRestrictedMessage");

        var participants = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(item => item.ConversationId == message.ConversationId &&
                item.JoinedUtc <= message.SentUtc &&
                (item.LeftUtc == null || item.LeftUtc > message.SentUtc))
            .Select(item => new ConversationParticipantEligibilityRow(
                item.UserId,
                item.ParticipantType))
            .ToListAsync(cancellationToken);
        if (participants.Count == 0 || participants.Any(item =>
                string.IsNullOrWhiteSpace(item.UserId) ||
                item.ParticipantType is not (MessagingParticipantTypes.Agent or MessagingParticipantTypes.Client)))
        {
            return Ineligible("IneligibleConsentAmbiguous");
        }

        var normalizedParticipants = participants
            .Select(item => item with { UserId = item.UserId.Trim().ToLowerInvariant() })
            .Distinct()
            .ToArray();
        var profileKeys = await ResolveParticipantProfilesAsync(normalizedParticipants, cancellationToken);
        if (profileKeys is null)
            return Ineligible("IneligibleConsentAmbiguous");

        var profileIds = profileKeys.Select(item => item.ProfileId).ToArray();
        var settings = await _db.MobileProfileSettings
            .AsNoTracking()
            .Where(item => profileIds.Contains(item.ProfileId))
            .Select(item => new MobileLearningConsentRow(
                item.ProfileId,
                item.ParticipantType,
                item.AllowsConsentedTranslationLearning))
            .ToListAsync(cancellationToken);
        var consentByProfile = settings.ToDictionary(
            item => (item.ProfileId, item.ParticipantType),
            item => item.AllowsConsentedTranslationLearning);
        if (profileKeys.Any(profile =>
                !consentByProfile.TryGetValue((profile.ProfileId, profile.ParticipantType), out var allowed) ||
                !allowed))
        {
            return Ineligible("IneligibleConsent");
        }

        return new TranslationLearningEligibility(
            true,
            "Eligible",
            LegendConnectKnowledgeProvenance.ConsentedLiveTranslation);
    }

    private async Task<IReadOnlyList<ParticipantProfileKey>?> ResolveParticipantProfilesAsync(
        IReadOnlyCollection<ConversationParticipantEligibilityRow> participants,
        CancellationToken cancellationToken)
    {
        var agentUserIds = participants
            .Where(item => item.ParticipantType == MessagingParticipantTypes.Agent)
            .Select(item => item.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var clientUserIds = participants
            .Where(item => item.ParticipantType == MessagingParticipantTypes.Client)
            .Select(item => item.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var agents = await _db.AgentProfiles
            .AsNoTracking()
            .Where(item => item.IsActive && agentUserIds.Contains(item.AgentUserId.ToLower()))
            .Select(item => new ParticipantProfileIdentity(item.Id, item.AgentUserId.ToLower()))
            .ToListAsync(cancellationToken);
        var clients = await _db.ClientProfiles
            .AsNoTracking()
            .Where(item => clientUserIds.Contains(item.ClientUserId.ToLower()) ||
                (item.ExternalIdentityObjectId != null && clientUserIds.Contains(item.ExternalIdentityObjectId.ToLower())))
            .Select(item => new ClientParticipantProfileIdentity(
                item.Id,
                item.ClientUserId.ToLower(),
                item.ExternalIdentityObjectId == null ? null : item.ExternalIdentityObjectId.ToLower()))
            .ToListAsync(cancellationToken);

        var resolved = new List<ParticipantProfileKey>(participants.Count);
        foreach (var participant in participants)
        {
            var profileIds = participant.ParticipantType == MessagingParticipantTypes.Agent
                ? agents.Where(item => item.UserId == participant.UserId).Select(item => item.ProfileId).Distinct().ToArray()
                : clients.Where(item => item.ClientUserId == participant.UserId || item.ExternalIdentityObjectId == participant.UserId)
                    .Select(item => item.ProfileId).Distinct().ToArray();
            if (profileIds.Length != 1)
                return null;

            resolved.Add(new ParticipantProfileKey(profileIds[0], participant.ParticipantType));
        }

        return resolved.Distinct().Count() == participants.Count ? resolved : null;
    }

    private static TranslationLearningEligibility Ineligible(string state) =>
        new(false, state, "PrivateMessageOperationalTranslation");

    private sealed record MessageEligibilityRow(
        Guid ConversationId,
        DateTime SentUtc,
        bool IsDeleted,
        bool IsProtected);

    private sealed record PersistedTranslationRow(string TranslatedText, string Provider);

    private sealed record ConversationParticipantEligibilityRow(
        string UserId,
        string ParticipantType);

    private sealed record ParticipantProfileIdentity(Guid ProfileId, string UserId);

    private sealed record ClientParticipantProfileIdentity(
        Guid ProfileId,
        string ClientUserId,
        string? ExternalIdentityObjectId);

    private sealed record ParticipantProfileKey(Guid ProfileId, string ParticipantType);

    private sealed record MobileLearningConsentRow(
        Guid ProfileId,
        string ParticipantType,
        bool AllowsConsentedTranslationLearning);
}

internal sealed record TranslationLearningEligibility(
    bool IsEligible,
    string State,
    string Provenance);

/// <summary>
/// Failure-isolated event publisher. It runs only after the operational
/// MessageTranslation has been saved and it never throws back into messaging.
/// </summary>
internal sealed class LegendTranslationLearningPublisher : ITranslationLearningPublisher
{
    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _languages;
    private readonly LegendTranslationTrainingEligibilityPolicy _eligibility;
    private readonly ILogger<LegendTranslationLearningPublisher> _logger;

    public LegendTranslationLearningPublisher(
        MasterAppDbContext db,
        ILegendLanguageRegistry languages,
        ILogger<LegendTranslationLearningPublisher> logger)
    {
        _db = db;
        _languages = languages;
        _eligibility = new LegendTranslationTrainingEligibilityPolicy(_db);
        _logger = logger;
    }

    public async Task TryPublishAsync(
        TranslationLearningCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pair = await _languages.GetOrCreateEnabledPairAsync(
                candidate.SourceLanguageCode,
                candidate.TargetLanguageCode,
                cancellationToken);
            if (pair is null)
                return;

            var eligibility = await _eligibility.EvaluateAsync(
                candidate,
                pair.TargetLanguageCode,
                cancellationToken);
            var key = $"message:{candidate.SourceMessageId:D}:{pair.PairKey}";
            if (await _db.Set<LegendTranslationLearningEvent>()
                    .AnyAsync(item => item.IdempotencyKey == key, cancellationToken))
                return;

            var item = new LegendTranslationLearningEvent
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = key,
                SourceMessageId = candidate.SourceMessageId,
                SourceLanguageCode = pair.SourceLanguageCode,
                TargetLanguageCode = pair.TargetLanguageCode,
                PairKey = pair.PairKey,
                SourceTextHash = LegendLanguageIdentity.TextHash(candidate.SourceText),
                TargetTextHash = LegendLanguageIdentity.TextHash(candidate.TargetText),
                SourceText = eligibility.IsEligible ? candidate.SourceText : null,
                TargetText = eligibility.IsEligible ? candidate.TargetText : null,
                Provider = candidate.Provider,
                Provenance = eligibility.Provenance,
                EligibilityState = eligibility.State,
                ProcessingState = eligibility.IsEligible ? "Pending" : "Skipped",
                PromotionOutcome = eligibility.IsEligible ? null : "NotEligible",
                CreatedUtc = DateTime.UtcNow,
                ProcessedUtc = eligibility.IsEligible ? null : DateTime.UtcNow
            };
            _db.Set<LegendTranslationLearningEvent>().Add(item);
            await _db.SaveChangesAsync(cancellationToken);
            LegendConnectTelemetry.LearningEvent(eligibility.State);
        }
        catch (DbUpdateException)
        {
            // Unique IdempotencyKey means concurrent projections can publish
            // safely without creating duplicate corpus work.
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Legend Connect learning event publication failed. MessageId={MessageId}", candidate.SourceMessageId);
        }
    }
}

/// <summary>
/// Converts an already-governed event into isolated text units and a directional
/// alignment. It intentionally cannot read InternalMessages, so it is not an
/// alternate private-message retrieval path.
/// </summary>
internal sealed record LegendConnectFounderSeedBatchResult(
    IReadOnlyDictionary<string, LegendLanguageTextUnit> TextUnitsByHash,
    int CreatedUnitCount,
    int ReusedUnitCount,
    int QueuedCoverageCount);

internal sealed class LegendConnectCorpusService
{
    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _languages;
    private readonly ILogger<LegendConnectCorpusService> _logger;
    private readonly ILegendConnectOperationalEventWriter? _operations;
    private readonly ILegendConnectTranslationIntelligence? _intelligence;

    public LegendConnectCorpusService(
        MasterAppDbContext db,
        ILegendLanguageRegistry languages,
        ILogger<LegendConnectCorpusService> logger,
        ILegendConnectOperationalEventWriter? operations = null,
        ILegendConnectTranslationIntelligence? intelligence = null)
    {
        _db = db;
        _languages = languages;
        _logger = logger;
        _operations = operations;
        _intelligence = intelligence;
    }

    public async Task ProcessPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var eventIds = await _db.Set<LegendTranslationLearningEvent>()
            .Where(item => item.EligibilityState == "Eligible" &&
                (item.ProcessingState == "Pending" ||
                 (item.ProcessingState == "Processing" && item.LeaseExpiresUtc != null && item.LeaseExpiresUtc < now)))
            .OrderBy(item => item.CreatedUtc)
            .Take(Math.Clamp(take, 1, 100))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        foreach (var eventId in eventIds)
        {
            var item = await TryClaimEventAsync(eventId, cancellationToken);
            if (item is not null)
                await ProcessAsync(item, cancellationToken);
        }
    }

    /// <summary>
    /// Keeps the pair projection derived from the canonical active-alignment
    /// lineage. Submission, correction, and historical reconciliation all use
    /// this one calculation rather than maintaining competing counters.
    /// </summary>
    internal async Task RefreshPairCoverageAsync(string pairKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pairKey))
            return;

        var pair = await _db.Set<LegendLanguagePair>()
            .SingleOrDefaultAsync(item => item.PairKey == pairKey, cancellationToken);
        if (pair is null)
            return;

        pair.CorpusCoverage = await (
            from alignment in _db.Set<LegendTranslationAlignment>()
            join source in _db.Set<LegendLanguageTextUnit>() on alignment.SourceTextUnitId equals source.Id
            join target in _db.Set<LegendLanguageTextUnit>() on alignment.TargetTextUnitId equals target.Id
            where alignment.PairKey == pairKey && alignment.SupersededUtc == null &&
                source.IsTrainingEligible && target.IsTrainingEligible
            select alignment.Id
        ).CountAsync(cancellationToken);
        pair.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ProcessAsync(LegendTranslationLearningEvent item, CancellationToken cancellationToken = default)
    {
        if (item.ProcessingState is not ("Pending" or "Processing") || item.EligibilityState != "Eligible" ||
            string.IsNullOrWhiteSpace(item.SourceText) || string.IsNullOrWhiteSpace(item.TargetText))
            return;

        try
        {
            var retiredSourceExists = await _db.Set<LegendLanguageTextUnit>().AsNoTracking().AnyAsync(unit =>
                unit.LanguageCode == item.SourceLanguageCode &&
                unit.NormalizedHash == item.SourceTextHash &&
                !unit.IsTrainingEligible,
                cancellationToken);
            if (retiredSourceExists)
            {
                item.ProcessingState = "Superseded";
                item.PromotionOutcome = "Superseded";
                item.FailureCode = "source_entry_unavailable";
                item.ProcessedUtc = DateTime.UtcNow;
                item.LeaseExpiresUtc = null;
                await _db.SaveChangesAsync(cancellationToken);
                LegendConnectTelemetry.CorpusEvent(item.ProcessingState, item.PairKey);
                return;
            }

            // Provider-derived targets are only valid as expansions of an
            // existing canonical source asset. Never recreate a source from a
            // queued payload: doing so would sever the target's atomic
            // provenance and reintroduce a second ingestion boundary.
            if (string.Equals(item.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal) &&
                (LegendLanguageIdentity.TextHash(item.SourceText) != item.SourceTextHash ||
                 !await _db.Set<LegendLanguageTextUnit>().AsNoTracking().AnyAsync(unit =>
                     unit.LanguageCode == item.SourceLanguageCode &&
                     unit.NormalizedHash == item.SourceTextHash &&
                     unit.IsTrainingEligible,
                     cancellationToken)))
            {
                item.ProcessingState = "Superseded";
                item.PromotionOutcome = "Superseded";
                item.FailureCode = "source_entry_unavailable";
                item.ProcessedUtc = DateTime.UtcNow;
                item.LeaseExpiresUtc = null;
                await _db.SaveChangesAsync(cancellationToken);
                LegendConnectTelemetry.CorpusEvent(item.ProcessingState, item.PairKey);
                return;
            }

            var pair = await _languages.GetOrCreateEnabledPairAsync(
                item.SourceLanguageCode,
                item.TargetLanguageCode,
                cancellationToken);
            if (pair is null)
            {
                item.ProcessingState = "Rejected";
                item.PromotionOutcome = "Rejected";
                item.FailureCode = "language_pair_unavailable";
                item.ProcessedUtc = DateTime.UtcNow;
                item.LeaseExpiresUtc = null;
                await _db.SaveChangesAsync(cancellationToken);
                if (_operations is not null)
                {
                    await _operations.TryRecordAsync(
                        "PairProvisioning",
                        "Error",
                        "Rejected",
                        item.SourceLanguageCode,
                        item.PairKey,
                        "language_pair_unavailable",
                        summary: "The approved learning event could not resolve an enabled directional pair.",
                        cancellationToken: cancellationToken);
                }
                LegendConnectTelemetry.CorpusEvent(item.ProcessingState, item.PairKey);
                return;
            }

            var source = await GetOrCreateTextUnitAsync(
                pair.SourceLanguageCode,
                item.SourceText,
                item.SourceTextHash,
                item.Provenance,
                cancellationToken);
            var target = await GetOrCreateTextUnitAsync(
                pair.TargetLanguageCode,
                item.TargetText,
                item.TargetTextHash,
                item.Provenance,
                cancellationToken);
            var alignment = await _db.Set<LegendTranslationAlignment>()
                .SingleOrDefaultAsync(candidate => candidate.PairKey == pair.PairKey &&
                    candidate.SourceTextUnitId == source.Id && candidate.TargetTextUnitId == target.Id,
                    cancellationToken);
            var alignmentCreated = alignment is null;
            if (alignmentCreated)
            {
                var consentedLiveTranslation = string.Equals(
                    item.Provenance,
                    LegendConnectKnowledgeProvenance.ConsentedLiveTranslation,
                    StringComparison.Ordinal);
                alignment = new LegendTranslationAlignment
                {
                    Id = Guid.NewGuid(),
                    PairKey = pair.PairKey,
                    SourceTextUnitId = source.Id,
                    TargetTextUnitId = target.Id,
                    Provider = item.Provider,
                    Provenance = item.Provenance,
                    // A consented successful provider translation is eligible
                    // for exact-match reuse only. It is deliberately not
                    // HumanVerified and does not make contextual composition
                    // production-eligible by itself.
                    Confidence = consentedLiveTranslation ? 0.98m : null,
                    QualityState = consentedLiveTranslation ? "ConsentedLive" : "Observation",
                    ObservationCount = 1,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                _db.Set<LegendTranslationAlignment>().Add(alignment);
            }
            else
            {
                alignment!.ObservationCount++;
                alignment.UpdatedUtc = DateTime.UtcNow;
                if (!alignment.HumanVerified &&
                    string.Equals(item.Provenance, LegendConnectKnowledgeProvenance.ConsentedLiveTranslation, StringComparison.Ordinal))
                {
                    alignment.Confidence = Math.Max(alignment.Confidence ?? 0m, 0.98m);
                    if (!string.Equals(alignment.QualityState, "Verified", StringComparison.Ordinal))
                        alignment.QualityState = "ConsentedLive";
                }
            }

            await GetOrCreateContextRelationshipAsync(
                pair.PairKey,
                source,
                target,
                contextCategory: null,
                usageRegister: null,
                regionalVariant: null,
                confidence: alignment.HumanVerified ? 1m : 0.5m,
                qualityState: alignment.HumanVerified ? "Verified" : "Observation",
                provenance: item.Provenance,
                cancellationToken);

            var pairEntity = await _db.Set<LegendLanguagePair>()
                .SingleAsync(candidate => candidate.PairKey == pair.PairKey, cancellationToken);
            if (alignment.HumanVerified)
                pairEntity.QualityState = "Validated";
            pairEntity.UpdatedUtc = DateTime.UtcNow;

            item.ProcessingState = "Processed";
            item.ProcessedUtc = DateTime.UtcNow;
            item.LeaseExpiresUtc = null;
            item.FailureCode = null;
            item.PromotionOutcome = alignmentCreated ? "Promoted" : "Reused";
            await _db.SaveChangesAsync(cancellationToken);
            await RefreshPairCoverageAsync(pair.PairKey, cancellationToken);
            if (_intelligence is not null &&
                string.Equals(item.Provenance, LegendConnectKnowledgeProvenance.ProviderDerived, StringComparison.Ordinal))
            {
                try
                {
                    // Corpus processing is asynchronous to messaging. Quality
                    // analysis stays on this governed learning path and must
                    // never turn a provider observation into a retry-triggering
                    // or latency-sensitive delivery dependency.
                    await _intelligence.EvaluateProviderObservationAsync(alignment.Id, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(exception,
                        "Legend Connect provider-quality evaluation was deferred after corpus persistence. AlignmentId={AlignmentId}",
                        alignment.Id);
                    if (_operations is not null)
                    {
                        await _operations.TryRecordAsync(
                            "TranslationQuality",
                            "Warning",
                            "Deferred",
                            pair.SourceLanguageCode,
                            pair.PairKey,
                            "quality_evaluation_deferred",
                            summary: "Provider observation quality evaluation was deferred after canonical corpus persistence.",
                            cancellationToken: CancellationToken.None);
                    }
                }
            }
            LegendConnectTelemetry.CorpusEvent(item.ProcessingState, item.PairKey);
        }
        catch (DbUpdateException exception)
        {
            _logger.LogWarning(exception, "Legend Connect corpus event conflicted and will be retried. EventId={EventId}", item.Id);
            _db.ChangeTracker.Clear();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Legend Connect corpus event failed. EventId={EventId}", item.Id);
            item.AttemptCount++;
            item.FailureCode = "corpus_processing_failed";
            item.ProcessingState = "Pending";
            item.LeaseExpiresUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            if (_operations is not null)
            {
                await _operations.TryRecordAsync(
                    "CorpusProcessing",
                    "Error",
                    "Failed",
                    item.SourceLanguageCode,
                    item.PairKey,
                    "corpus_processing_failed",
                    summary: "Approved learning processing failed and was returned to the retry queue.",
                    cancellationToken: CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Phase-5 admission for knowledge already proven by the Phase-4 canonical
    /// machine validator. This extends the existing corpus authority rather
    /// than creating another corpus path.
    ///
    /// Existing stronger provenance is never downgraded. New machine
    /// alignments are SystemValidated but never HumanVerified.
    /// </summary>
    internal async Task<LegendConnectKnowledgeSubmissionResult>
        SubmitSystemValidatedMachineKnowledgeAsync(
            string sourceLanguageCode,
            string sourceText,
            string? targetLanguageCode,
            string? targetText,
            string? contextCategory,
            CancellationToken cancellationToken = default)
    {
        var sourceLanguage =
            await _languages.NormalizeEnabledTranslationLanguageAsync(
                sourceLanguageCode,
                cancellationToken);

        var normalizedSource =
            LegendLanguageIdentity.NormalizeText(sourceText);

        if (sourceLanguage is null ||
            string.IsNullOrWhiteSpace(normalizedSource) ||
            normalizedSource.Length > 10_000)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false,
                false,
                "invalid_machine_source",
                "The SystemValidatedMachine source failed canonical normalization.",
                sourceLanguage ?? string.Empty,
                null,
                null,
                null,
                null,
                null);
        }

        var hasTargetLanguage =
            !string.IsNullOrWhiteSpace(targetLanguageCode);
        var hasTargetText =
            !string.IsNullOrWhiteSpace(targetText);

        if (hasTargetLanguage != hasTargetText)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false,
                false,
                "invalid_machine_pair",
                "A machine-validated directional entry requires both target language and target text.",
                sourceLanguage,
                null,
                null,
                null,
                null,
                null);
        }

        string? targetLanguage = null;
        string? normalizedTarget = null;
        LegendLanguagePairSnapshot? pair = null;

        if (hasTargetLanguage)
        {
            targetLanguage =
                await _languages.NormalizeEnabledTranslationLanguageAsync(
                    targetLanguageCode,
                    cancellationToken);

            normalizedTarget =
                LegendLanguageIdentity.NormalizeText(targetText!);

            if (targetLanguage is null ||
                string.IsNullOrWhiteSpace(normalizedTarget) ||
                normalizedTarget.Length > 10_000 ||
                string.Equals(
                    sourceLanguage,
                    targetLanguage,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new LegendConnectKnowledgeSubmissionResult(
                    false,
                    false,
                    "invalid_machine_pair",
                    "The SystemValidatedMachine directional pair is invalid.",
                    sourceLanguage,
                    targetLanguage,
                    null,
                    null,
                    null,
                    null);
            }

            pair = await _languages.GetOrCreateEnabledPairAsync(
                sourceLanguage,
                targetLanguage,
                cancellationToken);

            if (pair is null)
            {
                return new LegendConnectKnowledgeSubmissionResult(
                    false,
                    false,
                    "machine_pair_unavailable",
                    "The SystemValidatedMachine directional pair is unavailable.",
                    sourceLanguage,
                    targetLanguage,
                    null,
                    null,
                    null,
                    null);
            }
        }

        var sourceHash =
            LegendLanguageIdentity.TextHash(normalizedSource);

        var existingSource =
            await _db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(
                    item =>
                        item.LanguageCode == sourceLanguage &&
                        item.NormalizedHash == sourceHash,
                    cancellationToken);

        if (existingSource is not null &&
            !existingSource.IsTrainingEligible)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false,
                false,
                "machine_source_retired",
                "The machine proposal matches a retired canonical source asset.",
                sourceLanguage,
                targetLanguage,
                pair?.PairKey,
                existingSource.Id,
                null,
                null);
        }

        var source =
            existingSource ??
            await GetOrCreateTextUnitAsync(
                sourceLanguage,
                normalizedSource,
                sourceHash,
                LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                cancellationToken);

        if (pair is null || normalizedTarget is null)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                true,
                existingSource is not null,
                null,
                existingSource is null
                    ? "SystemValidatedMachine source entered the existing corpus."
                    : "Existing canonical source was reused without changing its stronger/original provenance.",
                sourceLanguage,
                null,
                null,
                source.Id,
                null,
                null);
        }

        var targetHash =
            LegendLanguageIdentity.TextHash(normalizedTarget);

        var existingTarget =
            await _db.Set<LegendLanguageTextUnit>()
                .SingleOrDefaultAsync(
                    item =>
                        item.LanguageCode == pair.TargetLanguageCode &&
                        item.NormalizedHash == targetHash,
                    cancellationToken);

        if (existingTarget is not null &&
            !existingTarget.IsTrainingEligible)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false,
                false,
                "machine_target_retired",
                "The machine proposal matches a retired canonical target asset.",
                sourceLanguage,
                pair.TargetLanguageCode,
                pair.PairKey,
                source.Id,
                existingTarget.Id,
                null);
        }

        var target =
            existingTarget ??
            await GetOrCreateTextUnitAsync(
                pair.TargetLanguageCode,
                normalizedTarget,
                targetHash,
                LegendConnectKnowledgeProvenance.SystemValidatedMachine,
                cancellationToken);

        // Founder/HumanVerified directional knowledge always wins.
        var founderConflict =
            await _db.Set<LegendTranslationAlignment>()
                .AsNoTracking()
                .AnyAsync(
                    item =>
                        item.PairKey == pair.PairKey &&
                        item.SourceTextUnitId == source.Id &&
                        item.TargetTextUnitId != target.Id &&
                        item.HumanVerified &&
                        item.SupersededUtc == null,
                    cancellationToken);

        if (founderConflict)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false,
                false,
                "human_verified_directional_conflict",
                "A stronger HumanVerified target blocks this machine admission.",
                sourceLanguage,
                pair.TargetLanguageCode,
                pair.PairKey,
                source.Id,
                target.Id,
                null);
        }

        var alignment =
            await _db.Set<LegendTranslationAlignment>()
                .SingleOrDefaultAsync(
                    item =>
                        item.PairKey == pair.PairKey &&
                        item.SourceTextUnitId == source.Id &&
                        item.TargetTextUnitId == target.Id &&
                        item.SupersededUtc == null,
                    cancellationToken);

        if (alignment is not null && !alignment.HumanVerified)
        {
            var contradicted =
                await _db.Set<LegendTranslationQualityEvidence>()
                    .AsNoTracking()
                    .AnyAsync(
                        item =>
                            item.ObservedAlignmentId == alignment.Id &&
                            item.Signal == "Contradictory" &&
                            item.ResolutionState == "Open" &&
                            item.SupersededUtc == null,
                        cancellationToken);

            if (contradicted)
            {
                return new LegendConnectKnowledgeSubmissionResult(
                    false,
                    false,
                    "machine_alignment_contradicted",
                    "An unresolved contradiction blocks machine admission.",
                    sourceLanguage,
                    pair.TargetLanguageCode,
                    pair.PairKey,
                    source.Id,
                    target.Id,
                    alignment.Id);
            }

            alignment.QualityState = "SystemValidated";
            alignment.Confidence =
                Math.Max(alignment.Confidence ?? 0m, 0.98m);
            alignment.UpdatedUtc = DateTime.UtcNow;
        }

        if (alignment is null)
        {
            alignment = new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = pair.PairKey,
                SourceTextUnitId = source.Id,
                TargetTextUnitId = target.Id,
                Provider = "LegendSystemValidator",
                Provenance =
                    LegendConnectKnowledgeProvenance
                        .SystemValidatedMachine,
                Confidence = 0.98m,
                QualityState = "SystemValidated",
                HumanVerified = false,
                ObservationCount = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            _db.Set<LegendTranslationAlignment>().Add(alignment);
        }

        await GetOrCreateContextRelationshipAsync(
            pair.PairKey,
            source,
            target,
            contextCategory,
            usageRegister: null,
            regionalVariant: null,
            confidence: alignment.HumanVerified ? 1m : 0.98m,
            qualityState:
                alignment.HumanVerified
                    ? "Verified"
                    : "SystemValidated",
            provenance:
                alignment.HumanVerified
                    ? alignment.Provenance
                    : LegendConnectKnowledgeProvenance
                        .SystemValidatedMachine,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await RefreshPairCoverageAsync(
            pair.PairKey,
            cancellationToken);

        return new LegendConnectKnowledgeSubmissionResult(
            true,
            existingSource is not null &&
                existingTarget is not null,
            null,
            alignment.HumanVerified
                ? "Existing HumanVerified alignment was reused without downgrade."
                : "SystemValidatedMachine directional knowledge entered the existing corpus without HumanVerified authority.",
            sourceLanguage,
            pair.TargetLanguageCode,
            pair.PairKey,
            source.Id,
            target.Id,
            alignment.Id);
    }

    /// <summary>
    /// Founder-approved knowledge enters the very same isolated text-unit,
    /// alignment, and contextual-intelligence pipeline as approved automated
    /// corpus work. Exact canonical duplicates are rejected before any insert
    /// and enforced again by the database uniqueness boundary.
    /// </summary>
    internal async Task<LegendConnectKnowledgeSubmissionResult> SubmitApprovedKnowledgeAsync(
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? reusableSourceTextUnitId = null,
        Guid? reusableTargetTextUnitId = null,
        bool queueFounderExpansion = true)
    {
        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
            submission.SourceLanguageCode,
            cancellationToken);
        var sourceText = LegendLanguageIdentity.NormalizeText(submission.SourceText);
        if (sourceLanguage is null || string.IsNullOrWhiteSpace(sourceText) || sourceText.Length > 10_000)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "invalid_source_entry", "An enabled source language and non-empty text are required.",
                sourceLanguage ?? string.Empty, null, null, null, null, null);
        }

        var hasTargetLanguage = !string.IsNullOrWhiteSpace(submission.TargetLanguageCode);
        var hasTargetText = !string.IsNullOrWhiteSpace(submission.TargetText);
        if (hasTargetLanguage != hasTargetText)
        {
            return new LegendConnectKnowledgeSubmissionResult(
                false, false, "invalid_pair_entry", "A translation pair requires both a target language and target text.",
                sourceLanguage, null, null, null, null, null);
        }

        string? targetLanguage = null;
        string? targetText = null;
        if (hasTargetLanguage)
        {
            targetLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(
                submission.TargetLanguageCode,
                cancellationToken);
            targetText = LegendLanguageIdentity.NormalizeText(submission.TargetText!);
            if (targetLanguage is null || string.IsNullOrWhiteSpace(targetText) || targetText.Length > 10_000 ||
                string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return new LegendConnectKnowledgeSubmissionResult(
                    false, false, "invalid_pair_entry", "An enabled, distinct target language and non-empty text are required.",
                    sourceLanguage, targetLanguage, null, null, null, null);
            }
        }

        var sourceHash = LegendLanguageIdentity.TextHash(sourceText);
        var targetHash = targetText is null ? null : LegendLanguageIdentity.TextHash(targetText);
        LegendLanguageTextUnit? reusableSource = null;
        if (reusableSourceTextUnitId is not null)
        {
            reusableSource = await _db.Set<LegendLanguageTextUnit>().SingleOrDefaultAsync(item =>
                item.Id == reusableSourceTextUnitId &&
                item.IsTrainingEligible &&
                item.LanguageCode == sourceLanguage &&
                item.NormalizedHash == sourceHash, cancellationToken);
            if (reusableSource is null)
            {
                return new LegendConnectKnowledgeSubmissionResult(
                    false, false, "correction_source_mismatch",
                    "The correction source must match the active approved source entry.",
                    sourceLanguage, targetLanguage, null, null, null, null);
            }
        }
        LegendLanguageTextUnit? reusableTarget = null;
        if (reusableTargetTextUnitId is not null)
        {
            reusableTarget = await _db.Set<LegendLanguageTextUnit>().SingleOrDefaultAsync(item =>
                item.Id == reusableTargetTextUnitId &&
                item.IsTrainingEligible &&
                item.LanguageCode == targetLanguage &&
                item.NormalizedHash == targetHash, cancellationToken);
            if (reusableTarget is null)
            {
                return new LegendConnectKnowledgeSubmissionResult(
                    false, false, "correction_target_mismatch",
                    "The verified target must match the active canonical target entry.",
                    sourceLanguage, targetLanguage, null, reusableSource?.Id, null, null);
            }
        }
        if (reusableSource is null && await _db.Set<LegendLanguageTextUnit>().AnyAsync(item =>
                item.LanguageCode == sourceLanguage && item.NormalizedHash == sourceHash, cancellationToken))
        {
            return Duplicate(sourceLanguage, targetLanguage, "This exact entry already exists in this language.");
        }
        if (reusableTarget is null && targetLanguage is not null && targetHash is not null &&
            await _db.Set<LegendLanguageTextUnit>().AnyAsync(item =>
                item.LanguageCode == targetLanguage && item.NormalizedHash == targetHash, cancellationToken))
        {
            return Duplicate(sourceLanguage, targetLanguage, "This exact entry already exists in this language.");
        }

        // Founder corrections own a single transaction spanning the replacement
        // write, supersession, and audit. Reuse it when present so the corpus
        // pipeline remains the one canonical write path without committing a
        // replacement alignment before its predecessor is superseded.
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null;
        if (ownsTransaction)
            transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var source = reusableSource ?? await LegendConnectCanonicalCurriculumPersistence.AdmitTextUnitAsync(
                _db,
                sourceLanguage,
                sourceText,
                sourceHash,
                NormalizeRequired(submission.Provenance, 80),
                LegendLanguageIdentity.DatasetNamespace(sourceLanguage),
                cancellationToken);
            var sourceIdentityIsNew = reusableSource is null &&
                _db.Entry(source).State == EntityState.Added;

            LegendLanguageTextUnit? target = null;
            LegendTranslationAlignment? alignment = null;
            string? pairKey = null;
            if (targetLanguage is not null && targetText is not null && targetHash is not null)
            {
                target = reusableTarget ?? await LegendConnectCanonicalCurriculumPersistence.AdmitTextUnitAsync(
                    _db,
                    targetLanguage,
                    targetText,
                    targetHash,
                    NormalizeRequired(submission.Provenance, 80),
                    LegendLanguageIdentity.DatasetNamespace(targetLanguage),
                    cancellationToken);
                var pair = await _languages.GetOrCreateEnabledPairAsync(sourceLanguage, targetLanguage, cancellationToken);
                if (pair is null)
                {
                    if (transaction is not null)
                        await transaction.RollbackAsync(cancellationToken);
                    return new LegendConnectKnowledgeSubmissionResult(
                        false, false, "language_pair_unavailable", "The selected directional pair is not enabled.",
                        sourceLanguage, targetLanguage, null, null, null, null);
                }

                pairKey = pair.PairKey;
                alignment = new LegendTranslationAlignment
                {
                    Id = Guid.NewGuid(),
                    PairKey = pair.PairKey,
                    SourceTextUnitId = source.Id,
                    TargetTextUnitId = target.Id,
                    Provider = "FounderApproved",
                    Provenance = submission.Provenance,
                    Confidence = 1m,
                    QualityState = "Verified",
                    HumanVerified = true,
                    ObservationCount = 1,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                _db.Set<LegendTranslationAlignment>().Add(alignment);
                await GetOrCreateContextRelationshipAsync(
                    pair.PairKey,
                    source,
                    target,
                    submission.ContextCategory,
                    submission.UsageRegister,
                    submission.RegionalVariant,
                    1m,
                    "Verified",
                    submission.Provenance,
                    cancellationToken,
                    sourceIdentityIsNew);
            }
            else
            {
                await GetOrCreateContextRelationshipAsync(
                    null,
                    source,
                    source,
                    submission.ContextCategory,
                    submission.UsageRegister,
                    submission.RegionalVariant,
                    1m,
                    "Verified",
                    submission.Provenance,
                    cancellationToken,
                    sourceIdentityIsNew);
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (targetLanguage is null && queueFounderExpansion)
            {
                // A monolingual Founder-approved entry is an approved seed,
                // not a terminal dead end. Project it into the existing
                // candidate authority for valid enabled targets so the
                // existing planner/worker can expand only missing knowledge
                // under its normal capacity, quality, and deduplication gates.
                await EnsureFounderSeedCandidatesAsync(
                    source,
                    null,
                    null,
                    cancellationToken,
                    sourceIdentityIsNew);
            }
            if (pairKey is not null)
            {
                var pairEntity = await _db.Set<LegendLanguagePair>()
                    .SingleAsync(item => item.PairKey == pairKey, cancellationToken);
                pairEntity.QualityState = "Validated";
                pairEntity.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await RefreshPairCoverageAsync(pairKey, cancellationToken);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new LegendConnectKnowledgeSubmissionResult(
                true, false, null, null, sourceLanguage, targetLanguage, pairKey,
                source.Id, target?.Id, alignment?.Id);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return Duplicate(sourceLanguage, targetLanguage, "This exact entry already exists in this language.");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Batches atomic Founder source seeds through the existing canonical text
    /// unit, context, candidate, planner, and capacity path. It does not call
    /// a provider and does not create another acquisition queue.
    /// </summary>
    internal async Task<LegendConnectFounderSeedBatchResult> SubmitApprovedSourceSeedsAsync(
        string sourceLanguageCode,
        IReadOnlyList<string> sourceTexts,
        string? contextCategory,
        string? usageRegister,
        string? regionalVariant,
        CancellationToken cancellationToken = default)
    {
        var sourceLanguage = await _languages.NormalizeEnabledTranslationLanguageAsync(sourceLanguageCode, cancellationToken);
        if (sourceLanguage is null)
            throw new ArgumentException("An enabled source language is required.", nameof(sourceLanguageCode));

        var normalizedByHash = sourceTexts
            .Select(LegendLanguageIdentity.NormalizeText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => (Text: text, Hash: LegendLanguageIdentity.TextHash(text)))
            .DistinctBy(item => item.Hash, StringComparer.Ordinal)
            .ToList();
        if (normalizedByHash.Count is 0 or > 500 || normalizedByHash.Any(item => item.Text.Length > 2_000))
            throw new ArgumentException("Founder training must contain 1–500 atomic units of at most 2,000 characters each.", nameof(sourceTexts));

        var hashes = normalizedByHash.Select(item => item.Hash).ToArray();
        var existing = new Dictionary<string, LegendLanguageTextUnit>(StringComparer.Ordinal);
        var createdCount = 0;
        foreach (var item in normalizedByHash)
        {
            var textUnit = await LegendConnectCanonicalCurriculumPersistence.AdmitTextUnitAsync(
                _db,
                sourceLanguage,
                item.Text,
                item.Hash,
                "FounderApproved",
                LegendLanguageIdentity.DatasetNamespace(sourceLanguage),
                cancellationToken);
            existing[item.Hash] = textUnit;
            if (_db.Entry(textUnit).State == EntityState.Added)
                createdCount++;
        }
        if (createdCount > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var textUnits = normalizedByHash.ToDictionary(item => item.Hash, item => existing[item.Hash], StringComparer.Ordinal);
        var textUnitIds = textUnits.Values.Select(item => item.Id).ToArray();
        foreach (var source in textUnits.Values)
        {
            await GetOrCreateContextRelationshipAsync(
                null,
                source,
                source,
                contextCategory,
                usageRegister,
                regionalVariant,
                1m,
                "Verified",
                "FounderApproved",
                cancellationToken,
                incrementExistingObservation: false);
        }
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);

        var enabledTargets = await _languages.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var pairs = new List<LegendLanguagePairSnapshot>();
        foreach (var target in enabledTargets.Where(item => item.IsLearningEnabled && item.IsTranslationEnabled &&
                     !string.Equals(item.Code, sourceLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            var pair = await _languages.GetOrCreateEnabledPairAsync(sourceLanguage, target.Code, cancellationToken);
            if (pair is not null && !string.Equals(pair.SourceLanguageCode, pair.TargetLanguageCode, StringComparison.OrdinalIgnoreCase))
                pairs.Add(pair);
        }

        var pairKeys = pairs.Select(item => item.PairKey).ToArray();
        var activeAlignments = pairKeys.Length == 0
            ? new HashSet<(string PairKey, Guid SourceId)>()
            : (await (
                from alignment in _db.Set<LegendTranslationAlignment>()
                join target in _db.Set<LegendLanguageTextUnit>() on alignment.TargetTextUnitId equals target.Id
                where pairKeys.Contains(alignment.PairKey) && textUnitIds.Contains(alignment.SourceTextUnitId) &&
                    alignment.SupersededUtc == null && target.IsTrainingEligible
                select new { alignment.PairKey, alignment.SourceTextUnitId })
                .ToListAsync(cancellationToken))
                .Select(item => (item.PairKey, item.SourceTextUnitId))
                .ToHashSet();
        var targetCodes = pairs.Select(item => item.TargetLanguageCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existingCandidates = targetCodes.Length == 0
            ? new Dictionary<string, LegendCorpusCandidate>(StringComparer.Ordinal)
            : (await _db.Set<LegendCorpusCandidate>()
                .Where(item => item.SourceLanguageCode == sourceLanguage &&
                    targetCodes.Contains(item.TargetLanguageCode))
                .ToListAsync(cancellationToken))
                .ToDictionary(item => item.IdempotencyKey, StringComparer.Ordinal);
        var queued = 0;
        foreach (var source in textUnits.Values)
        {
            foreach (var pair in pairs)
            {
                if (activeAlignments.Contains((pair.PairKey, source.Id)))
                    continue;
                var idempotencyKey = $"founder-seed:{source.Id:D}:{pair.PairKey}";
                if (existingCandidates.TryGetValue(idempotencyKey, out var existingCandidate))
                {
                    if (RestoreCandidateForMissingCoverage(existingCandidate, source, pair))
                        queued++;
                    continue;
                }
                _db.Set<LegendCorpusCandidate>().Add(new LegendCorpusCandidate
                {
                    Id = Guid.NewGuid(),
                    IdempotencyKey = idempotencyKey,
                    SourceLanguageCode = pair.SourceLanguageCode,
                    TargetLanguageCode = pair.TargetLanguageCode,
                    SourceText = source.Text,
                    SourceTextHash = source.NormalizedHash,
                    Category = "FounderApprovedSeed",
                    Provenance = "FounderApproved",
                    IsApproved = true,
                    ProcessingState = "Pending",
                    CreatedUtc = DateTime.UtcNow
                });
                queued++;
            }
        }
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(cancellationToken);

        return new LegendConnectFounderSeedBatchResult(textUnits, createdCount, textUnits.Count - createdCount, queued);
    }

    private async Task<LegendTranslationLearningEvent?> TryClaimEventAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(5);
        if (!_db.Database.IsRelational())
        {
            var item = await _db.Set<LegendTranslationLearningEvent>()
                .SingleOrDefaultAsync(candidate => candidate.Id == eventId &&
                    (candidate.ProcessingState == "Pending" ||
                     (candidate.ProcessingState == "Processing" && candidate.LeaseExpiresUtc != null && candidate.LeaseExpiresUtc < now)), cancellationToken);
            if (item is null)
                return null;
            item.ProcessingState = "Processing";
            item.LeaseExpiresUtc = expires;
            await _db.SaveChangesAsync(cancellationToken);
            return item;
        }

        var claimed = await _db.Set<LegendTranslationLearningEvent>()
            .Where(candidate => candidate.Id == eventId &&
                (candidate.ProcessingState == "Pending" ||
                 (candidate.ProcessingState == "Processing" && candidate.LeaseExpiresUtc != null && candidate.LeaseExpiresUtc < now)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.ProcessingState, "Processing")
                .SetProperty(candidate => candidate.LeaseExpiresUtc, expires), cancellationToken);
        return claimed == 1
            ? await _db.Set<LegendTranslationLearningEvent>().SingleAsync(candidate => candidate.Id == eventId, cancellationToken)
            : null;
    }

    /// <summary>
    /// Uses the existing candidate authority to expand a Founder-owned source
    /// asset. Structured curriculum supplies optional lineage metadata; the
    /// provider worker, capacity ledger, and corpus processor remain unchanged.
    /// </summary>
    internal Task EnsureFounderSeedCandidatesAsync(
        LegendLanguageTextUnit source,
        Guid? curriculumFamilyId,
        Guid? sourceCurriculumExampleId,
        CancellationToken cancellationToken = default,
        bool sourceIdentityIsNew = false) =>
        QueueSeedCandidatesAsync(
            source,
            LegendConnectKnowledgeProvenance.FounderApproved,
            "FounderApprovedSeed",
            "founder-seed",
            cancellationToken,
            curriculumFamilyId,
            sourceCurriculumExampleId,
            sourceIdentityIsNew);

    internal Task EnsureSystemValidatedMachineSeedCandidatesAsync(
        LegendLanguageTextUnit source,
        Guid? curriculumFamilyId,
        Guid? sourceCurriculumExampleId,
        CancellationToken cancellationToken = default) =>
        QueueSeedCandidatesAsync(
            source,
            LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            "SystemValidatedMachineSeed",
            "system-validated-machine-seed",
            cancellationToken,
            curriculumFamilyId,
            sourceCurriculumExampleId);

    private async Task QueueSeedCandidatesAsync(
        LegendLanguageTextUnit source,
        string candidateProvenance,
        string category,
        string idempotencyPrefix,
        CancellationToken cancellationToken,
        Guid? curriculumFamilyId = null,
        Guid? sourceCurriculumExampleId = null,
        bool sourceIdentityIsNew = false)
    {
        if (!source.IsTrainingEligible)
            return;

        var enabledTargets =
            await _languages.ListEnabledTranslationLanguagesAsync(
                cancellationToken);

        var pending = false;

        foreach (var target in enabledTargets.Where(item =>
                     item.IsLearningEnabled &&
                     item.IsTranslationEnabled))
        {
            if (string.Equals(
                    source.LanguageCode,
                    target.Code,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pair =
                await _languages.GetOrCreateEnabledPairAsync(
                    source.LanguageCode,
                    target.Code,
                    cancellationToken);

            if (pair is null ||
                string.Equals(
                    pair.SourceLanguageCode,
                    pair.TargetLanguageCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var aligned = await (
                from alignment in _db.Set<LegendTranslationAlignment>()
                join targetUnit in _db.Set<LegendLanguageTextUnit>()
                    on alignment.TargetTextUnitId equals targetUnit.Id
                where
                    alignment.PairKey == pair.PairKey &&
                    alignment.SourceTextUnitId == source.Id &&
                    alignment.SupersededUtc == null &&
                    targetUnit.IsTrainingEligible
                select alignment.Id)
                .AnyAsync(cancellationToken);

            if (aligned)
                continue;

            // Reuse any existing exact source/direction candidate before
            // creating provenance-specific work. This prevents duplicate
            // Azure/provider calls when Founder, autonomous-gap, and machine
            // curriculum paths converge on the same missing coverage.
            // A source identity that has just crossed the text-unit uniqueness
            // boundary cannot already have any source/direction candidate.
            // Avoid a broad candidate lookup while parallel canonical family
            // transactions each hold unrelated new candidate rows; existing
            // source identities continue through the normal reuse lookup.
            var existingCandidate = sourceIdentityIsNew
                ? null
                : await _db.Set<LegendCorpusCandidate>()
                    .Where(item =>
                        item.SourceLanguageCode ==
                            pair.SourceLanguageCode &&
                        item.TargetLanguageCode ==
                            pair.TargetLanguageCode &&
                        item.SourceTextHash ==
                            source.NormalizedHash)
                    .OrderBy(item =>
                        item.IdempotencyKey ==
                            $"{idempotencyPrefix}:{source.Id:D}:{pair.PairKey}"
                            ? 0
                            : 1)
                    .ThenBy(item => item.CreatedUtc)
                    .FirstOrDefaultAsync(cancellationToken);

            if (existingCandidate is not null)
            {
                if (RestoreCandidateForMissingCoverage(
                        existingCandidate,
                        source,
                        pair))
                {
                    pending = true;
                }

                if (curriculumFamilyId is not null &&
                    existingCandidate.CurriculumFamilyId is null &&
                    existingCandidate.ProcessingState
                        is "Pending" or "Processing" or "Queued")
                {
                    existingCandidate.CurriculumFamilyId =
                        curriculumFamilyId;
                    existingCandidate.SourceCurriculumExampleId =
                        sourceCurriculumExampleId;
                    pending = true;
                }

                continue;
            }

            _db.Set<LegendCorpusCandidate>().Add(
                new LegendCorpusCandidate
                {
                    Id = Guid.NewGuid(),
                    IdempotencyKey =
                        $"{idempotencyPrefix}:{source.Id:D}:{pair.PairKey}",
                    SourceLanguageCode =
                        pair.SourceLanguageCode,
                    TargetLanguageCode =
                        pair.TargetLanguageCode,
                    SourceText = source.Text,
                    SourceTextHash =
                        source.NormalizedHash,
                    Category = category,
                    Provenance = candidateProvenance,
                    CurriculumFamilyId =
                        curriculumFamilyId,
                    SourceCurriculumExampleId =
                        sourceCurriculumExampleId,
                    IsApproved = true,
                    ProcessingState = "Pending",
                    CreatedUtc = DateTime.UtcNow
                });

            pending = true;
        }

        if (pending)
            await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool RestoreCandidateForMissingCoverage(
        LegendCorpusCandidate candidate,
        LegendLanguageTextUnit source,
        LegendLanguagePairSnapshot pair)
    {
        // Pending, leased, and provider-queued candidates remain owned by the
        // existing worker. Only terminal records that no longer have an active
        // alignment may resume their same durable candidate identity.
        if (candidate.ProcessingState is "Pending" or "Processing" or "Queued")
            return false;

        candidate.SourceLanguageCode = pair.SourceLanguageCode;
        candidate.TargetLanguageCode = pair.TargetLanguageCode;
        candidate.SourceText = source.Text;
        candidate.SourceTextHash = source.NormalizedHash;
        candidate.IsApproved = true;
        candidate.ProcessingState = "Pending";
        candidate.ProcessedUtc = null;
        candidate.LeaseExpiresUtc = null;
        candidate.FailureCode = null;
        return true;
    }

    private async Task<LegendLanguageTextUnit> GetOrCreateTextUnitAsync(
        string languageCode,
        string text,
        string hash,
        string provenance,
        CancellationToken cancellationToken)
    {
        var created = await LegendConnectCanonicalCurriculumPersistence.AdmitTextUnitAsync(
            _db,
            languageCode,
            LegendLanguageIdentity.NormalizeText(text),
            hash,
            NormalizeRequired(provenance, 80),
            LegendLanguageIdentity.DatasetNamespace(languageCode),
            cancellationToken);
        if (_db.Entry(created).State != EntityState.Added)
            return created;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            _db.Entry(created).State = EntityState.Detached;
            return await _db.Set<LegendLanguageTextUnit>()
                .SingleAsync(item => item.LanguageCode == languageCode && item.NormalizedHash == hash, cancellationToken);
        }
    }

    private async Task<LegendLanguageContextRelationship> GetOrCreateContextRelationshipAsync(
        string? pairKey,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit related,
        string? contextCategory,
        string? usageRegister,
        string? regionalVariant,
        decimal confidence,
        string qualityState,
        string provenance,
        CancellationToken cancellationToken,
        bool sourceIdentityIsNew = false,
        bool incrementExistingObservation = true)
    {
        var contextSignature = LegendLanguageIdentity.ContextSignature(
            contextCategory,
            usageRegister,
            regionalVariant);
        // A bounded evaluator may stage this exact canonical relationship
        // more than once before SaveChanges, so consult its tracked identity
        // first. A genuinely new source text-unit can skip the persisted
        // lookup, while a reused text-unit retains it; neither case may stage
        // a second local canonical row.
        var candidate = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(),
            PairKey = pairKey,
            SourceTextUnitId = source.Id,
            RelatedTextUnitId = related.Id,
            RelationshipKind = "ContextualExample",
            ContextSignature = contextSignature,
            SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(source.Text),
            ContextCategory = NormalizeOptional(contextCategory, 120),
            UsageRegister = NormalizeOptional(usageRegister, 80),
            RegionalVariant = NormalizeOptional(regionalVariant, 80),
            Confidence = Math.Clamp(confidence, 0m, 1m),
            QualityState = qualityState,
            Provenance = NormalizeRequired(provenance, 80),
            ObservationCount = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var existing = await LegendConnectCanonicalCurriculumPersistence.AdmitContextRelationshipAsync(
            _db,
            candidate,
            cancellationToken);
        if (existing is not null)
        {
            if (!ReferenceEquals(existing, candidate))
            {
                if (incrementExistingObservation)
                    existing.ObservationCount++;
                existing.Confidence = Math.Max(existing.Confidence, confidence);
                if (qualityState == "Verified")
                    existing.QualityState = "Verified";
                existing.UpdatedUtc = DateTime.UtcNow;
            }
            return existing;
        }
        throw new InvalidOperationException("Canonical context admission returned no relationship.");
    }

    private static LegendConnectKnowledgeSubmissionResult Duplicate(
        string sourceLanguage,
        string? targetLanguage,
        string message) => new(
            false,
            true,
            "duplicate_canonical_entry",
            message,
            sourceLanguage,
            targetLanguage,
            targetLanguage is null ? null : LegendLanguageIdentity.PairKey(sourceLanguage, targetLanguage),
            null,
            null,
            null);

    private static string NormalizeRequired(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "ApprovedKnowledge"
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }
}

/// <summary>
/// Deployment-owned marker for material changes to the canonical language
/// evaluator. Advance it only when a change alters derived language
/// intelligence; the runtime policy then sends active historical evidence
/// through this same hosted worker exactly once per version.
/// </summary>
internal static class LegendConnectLanguageIntelligenceEvaluatorVersion
{
    // v19 adds Founder-declared cross-example meaning relationships. The
    // existing evaluator derives only their structural transition projections
    // through the same replay/maturity authority; it neither invents a
    // response direction from family order nor requires Founder resubmission.
    // v20 makes the existing mature Founder meaning primitives and relations
    // eligible for the single governed Stage-6 content-binding authority.
    // Its contract is runtime-only: converged v19 canonical evidence is
    // reused through the dependency-driven lifecycle rather than broad replay.
    // v21 recompiles active Founder source families under the governed
    // executable-projection contract. Rich authored frames remain intact;
    // production may omit only non-conflicting static dimensions that the
    // present meaning graph does not expose, while ambiguity still fails
    // closed across the complete mature transition set.
    internal const int Current = 21;
}

internal sealed class LegendConnectLearningHostedService : BackgroundService
{
    // A replay item is the durable checkpoint unit. Keeping the page at one
    // stable identity means a later bad historical item can never erase
    // already committed replay progress, while the bounded loop still gives
    // the existing worker useful throughput in a single heartbeat.
    private const int HistoricalReplayItemsPerPage = 1;
    private const int MaximumHistoricalReplayPagesPerTick = 25;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LegendConnectLearningHostedService> _logger;
    private readonly HashSet<string> _phaseSeedRestartRecoveries = new(StringComparer.Ordinal);

    public LegendConnectLearningHostedService(
        IServiceScopeFactory scopes,
        ILogger<LegendConnectLearningHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Historical convergence is a production-readiness gate and
                // must never be starved by unrelated legacy, corpus, model,
                // or provider work. Run it first in its own scope on every
                // heartbeat. The durable lease authority remains the only
                // scheduler and recovers abandoned claims after restart.
                await ProcessHistoricalReevaluationCycleAsync(stoppingToken);

                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<LegendConnectFounderTrainingIngestionAuthority>()
                    .ReconcileLegacyAsync(25, stoppingToken);
                var runtime = scope.ServiceProvider.GetRequiredService<ILegendConnectRuntimePolicyAuthority>();
                if ((await runtime.GetEffectiveAsync(stoppingToken)).LearningEnabled)
                {
                    await scope.ServiceProvider.GetRequiredService<LegendConnectCorpusService>()
                        .ProcessPendingAsync(25, stoppingToken);
                }

                // Phase 7 reuses this existing deployment-wide learning
                // worker. The training service is configuration-gated and
                // bounded to one durable lifecycle transition per tick.
                await scope.ServiceProvider
                    .GetRequiredService<LegendConnectModelTrainingService>()
                    .ProcessOneAsync(stoppingToken);

                // Phase 8 continues the same durable model lifecycle only
                // after training has produced a challenger. Evaluation is
                // separately configuration-gated and cannot promote a model.
                await scope.ServiceProvider
                    .GetRequiredService<LegendConnectModelEvaluationService>()
                    .ProcessOneAsync(stoppingToken);

                // Phase 9 is the sole authority that may move an evaluated
                // challenger onto the existing pair ActiveModelVersion
                // projection. It remains separately configuration-gated and
                // does not participate in inference routing.
                await scope.ServiceProvider
                    .GetRequiredService<LegendConnectModelPromotionService>()
                    .ProcessOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Legend Connect learning worker failed.");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>
    /// Runs the exact historical-convergence responsibility used by the hosted
    /// service. Keeping this as one callable worker cycle lets regression tests
    /// exercise startup, durable claiming, expired-lease recovery, canonical
    /// evaluation, and forward phase advancement without manually completing
    /// work items or invoking the runtime cursor directly.
    /// </summary>
    internal async Task ProcessHistoricalReevaluationCycleAsync(
        CancellationToken stoppingToken = default)
    {
        using var scope = _scopes.CreateScope();
        var runtime = scope.ServiceProvider
            .GetRequiredService<ILegendConnectRuntimePolicyAuthority>();
        await runtime.RecordWorkerHeartbeatAsync("Learning", stoppingToken);
        var replay = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            stoppingToken);
        await ProcessHistoricalReevaluationAsync(
            scope.ServiceProvider,
            runtime,
            replay,
            stoppingToken);
    }

    private async Task ProcessHistoricalReevaluationAsync(
        IServiceProvider services,
        ILegendConnectRuntimePolicyAuthority runtime,
        LegendConnectLanguageIntelligenceReevaluationSnapshot replay,
        CancellationToken stoppingToken)
    {
        // A single healthy startup must be able to carry a drained phase into
        // its successor immediately. Bound the loop to the complete declared
        // phase vocabulary so an unexpected non-advancing state returns to the
        // normal heartbeat instead of becoming an in-process spin loop.
        const int maximumForwardPhaseTransitions = 6;
        for (var transition = 0;
             replay.RequiresWork && transition < maximumForwardPhaseTransitions;
             transition++)
        {
            var phase = replay.Phase;
            var work = services.GetRequiredService<LegendConnectHistoricalReevaluationWorkAuthority>();
            // A current evaluator that has reached ProviderObservations may retain
            // a real legacy cursor. Adopt that exact ordered suffix atomically
            // before choosing an executor; every new instance therefore observes
            // either the legacy stream or the durable work boundary, never both.
            if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations &&
                LegendConnectHistoricalReevaluationWorkAuthority.UsesCursorCompatibility(replay))
            {
                await work.TryAdoptProviderObservationsCursorAsync(replay, stoppingToken);
                replay = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
                    replay.TargetEvaluatorVersion,
                    stoppingToken);
            }

            if (LegendConnectHistoricalReevaluationWorkAuthority.UsesCursorCompatibility(replay))
            {
                await ProcessCursorCompatibleHistoricalReplayAsync(
                    services,
                    runtime,
                    replay,
                    stoppingToken);
            }
            else
            {
                await ProcessDynamicHistoricalReplayPhaseAsync(
                    runtime,
                    work,
                    replay,
                    stoppingToken);
            }

            var refreshed = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
                replay.TargetEvaluatorVersion,
                stoppingToken);
            if (!refreshed.RequiresWork ||
                string.Equals(refreshed.Phase, phase, StringComparison.Ordinal))
            {
                return;
            }
            replay = refreshed;
        }
    }

    /// <summary>
    /// The legacy cursor executor remains for earlier sequential phases of a
    /// replay that was already in flight at deployment. Once it reaches
    /// ProviderObservations, the caller atomically adopts its ordered suffix
    /// into durable work before this method can execute that phase.
    /// </summary>
    private static async Task ProcessCursorCompatibleHistoricalReplayAsync(
        IServiceProvider services,
        ILegendConnectRuntimePolicyAuthority runtime,
        LegendConnectLanguageIntelligenceReevaluationSnapshot replay,
        CancellationToken stoppingToken)
    {
        for (var replayPage = 0;
             replay.RequiresWork && replayPage < MaximumHistoricalReplayPagesPerTick;
             replayPage++)
        {
            LegendConnectHistoricalReevaluationProgress progress;
            if (replay.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
            {
                progress = await services.GetRequiredService<ILegendConnectTranslationIntelligence>()
                    .ReevaluateHistoricalProviderObservationsAsync(
                        HistoricalReplayItemsPerPage,
                        replay.Cursor,
                        stoppingToken);
            }
            else if (replay.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
            {
                progress = await services.GetRequiredService<ILegendConnectOperations>()
                    .ReconcileHistoricalOperationalTranslationsAsync(
                        HistoricalReplayItemsPerPage,
                        replay.Cursor,
                        stoppingToken);
            }
            else
            {
                progress = await services.GetRequiredService<LegendConnectCurriculumService>()
                    .ReevaluateHistoricalAlignmentsAsync(
                        HistoricalReplayItemsPerPage,
                        replay.Phase,
                        replay.Cursor,
                        stoppingToken);
            }

            await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                replay.Phase,
                progress.LastProcessedId,
                progress.PhaseComplete,
                stoppingToken);
            replay = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                stoppingToken);
        }
    }

    /// <summary>
    /// Keeps a bounded phase-local pool occupied with only durable,
    /// dependency-safe claims. The pool is scheduling capacity, never the
    /// correctness authority: leases and filtered SQL uniqueness protect work
    /// across process restarts and App Service instances.
    /// </summary>
    private async Task ProcessDynamicHistoricalReplayPhaseAsync(
        ILegendConnectRuntimePolicyAuthority runtime,
        LegendConnectHistoricalReevaluationWorkAuthority work,
        LegendConnectLanguageIntelligenceReevaluationSnapshot replay,
        CancellationToken stoppingToken)
    {
        var phase = replay.Phase;
        var evaluatorVersion = replay.TargetEvaluatorVersion;
        var workerPrefix = $"{Environment.MachineName}:historical:{Guid.NewGuid():N}";
        // A failed scheduler seed predates every canonical claim and can
        // otherwise strand an entirely healthy hosted worker forever. Give a
        // new process exactly one recovery for this evaluator/phase. The
        // canonical failures continue to fail closed through their existing
        // retry and retirement authority. A recovered seed clears its stale
        // error only after the normal completion lifecycle succeeds.
        var restartRecoveryKey = $"{evaluatorVersion}:{phase}";
        if (_phaseSeedRestartRecoveries.Add(restartRecoveryKey))
            await work.TryRecoverFailedPhaseSeedAsync(evaluatorVersion, phase, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var seeded = await work.SeedNextBatchAsync(
                evaluatorVersion,
                phase,
                workerPrefix + ":seed",
                stoppingToken);
            var slots = Enumerable.Range(0, work.MaximumConcurrency)
                .Select(slot => ProcessDynamicHistoricalReplaySlotAsync(
                    evaluatorVersion,
                    phase,
                    workerPrefix + ":" + slot,
                    stoppingToken))
                .ToArray();
            var processed = await Task.WhenAll(slots);

            var refreshed = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
                evaluatorVersion,
                stoppingToken);
            if (!refreshed.RequiresWork || !string.Equals(refreshed.Phase, phase, StringComparison.Ordinal))
                return;

            if (!seeded.MadeProgress && processed.All(count => count == 0))
                break;
        }

        await work.TryAdvancePhaseAsync(evaluatorVersion, phase, stoppingToken);
    }

    private async Task<int> ProcessDynamicHistoricalReplaySlotAsync(
        int evaluatorVersion,
        string phase,
        string workerId,
        CancellationToken stoppingToken)
    {
        var processed = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _scopes.CreateScope();
            var work = scope.ServiceProvider.GetRequiredService<LegendConnectHistoricalReevaluationWorkAuthority>();
            var claim = await work.TryClaimNextAsync(evaluatorVersion, phase, workerId, stoppingToken);
            if (claim is null)
                return processed;

            try
            {
                if (claim.SubjectId is not Guid subjectId)
                    throw new InvalidOperationException("A canonical historical work item requires a stable subject identity.");

                // The evaluator must own a database-held execution fence for
                // its entire canonical write interval.  A timestamp-only
                // lease cannot guarantee this: a slow evaluator could keep
                // writing after another instance reclaims an expired row.
                // The guard conditionally renews the exact token and holds a
                // SQL row lock through the evaluator and its completion.
                await using var execution = await work.TryBeginOwnedExecutionAsync(claim, stoppingToken);
                if (execution is null)
                {
                    _logger.LogWarning(
                        "Legend historical reevaluation ownership was lost before evaluation. EvaluatorVersion={EvaluatorVersion} Phase={Phase} WorkItemId={WorkItemId}",
                        evaluatorVersion,
                        phase,
                        claim.WorkItemId);
                    return processed;
                }

                if (claim.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.DerivationLedgerWorkKind)
                {
                    if (claim.SubjectId is not Guid familyId)
                        throw new InvalidOperationException("A derivation-ledger work item requires its canonical family identity.");
                    await scope.ServiceProvider.GetRequiredService<LegendConnectCurriculumService>()
                        .RefreshCurrentDerivationDependenciesForFamilyAsync(
                            familyId,
                            evaluatorVersion,
                            stoppingToken);
                }
                else if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.DependencyInventory)
                {
                    if (!int.TryParse(claim.SubjectScope, out var batchSize) || batchSize <= 0)
                        throw new InvalidOperationException("Dependency inventory work requires its bounded family count.");
                    var inventory = await scope.ServiceProvider
                        .GetRequiredService<LegendConnectCurriculumService>()
                        .InventoryHistoricalDerivationDependenciesBatchAsync(
                            subjectId == Guid.Empty ? null : subjectId,
                            evaluatorVersion,
                            batchSize,
                            stoppingToken);
                    if (inventory.LastFamilyId is null)
                        throw new InvalidOperationException("A claimed dependency inventory page had no remaining family identity.");
                    // This cursor update and the ledger writes participate in
                    // the same owned execution transaction. A process loss
                    // therefore leaves neither an unrecorded page nor a
                    // cursor that skipped canonical identities.
                    await scope.ServiceProvider
                        .GetRequiredService<ILegendConnectRuntimePolicyAuthority>()
                        .AdvanceLanguageIntelligenceReevaluationAsync(
                        evaluatorVersion,
                        phase,
                        inventory.LastFamilyId,
                        phaseComplete: false,
                        stoppingToken);
                }
                else if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
                {
                    await scope.ServiceProvider.GetRequiredService<ILegendConnectTranslationIntelligence>()
                        .ReevaluateHistoricalProviderObservationAsync(subjectId, stoppingToken);
                }
                else if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
                {
                    await scope.ServiceProvider.GetRequiredService<ILegendConnectOperations>()
                        .ReconcileHistoricalOperationalTranslationAsync(subjectId, stoppingToken);
                }
                else
                {
                    var curriculum = scope.ServiceProvider.GetRequiredService<LegendConnectCurriculumService>();
                    await curriculum.ReevaluateHistoricalWorkItemAsync(
                            phase,
                            subjectId,
                            claim.SubjectScope,
                            stoppingToken);
                    if (phase == LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies)
                    {
                        // Dependency ledger projection is a separately leased
                        // downstream family work item.  It is queued in this
                        // same owned transaction, then executes only after
                        // the phase's canonical family mutations drain.
                        await work.EnqueueFamilyDerivationLedgerAsync(claim, stoppingToken);
                    }
                }

                if (!await execution.CompleteAsync(stoppingToken))
                {
                    await execution.AbortAsync();
                    _logger.LogWarning(
                        "Legend historical reevaluation ownership was lost before completion. EvaluatorVersion={EvaluatorVersion} Phase={Phase} WorkItemId={WorkItemId}",
                        evaluatorVersion,
                        phase,
                        claim.WorkItemId);
                    return processed;
                }
                processed++;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The execution transaction has already rolled canonical
                // writes back before this conditional release makes the
                // identity recoverable.
                await work.ReleaseAsync(
                    claim,
                    "historical_reevaluation_execution_cancelled",
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await work.ReleaseAsync(
                    claim,
                    "historical_reevaluation_worker_cancelled",
                    CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                await work.FailAsync(
                    claim,
                    "historical_reevaluation_canonical_failure",
                    CancellationToken.None);
                _logger.LogWarning(
                    exception,
                    "Legend historical reevaluation work failed safely. EvaluatorVersion={EvaluatorVersion} Phase={Phase} WorkItemId={WorkItemId}",
                    evaluatorVersion,
                    phase,
                    claim.WorkItemId);
            }
        }

        return processed;
    }
}

/// <summary>
/// Plans ordinary autonomous work from explicitly approved candidates, real
/// directional demand, and current corpus coverage. It has no named-language
/// rules and never treats private operational message text as a candidate.
/// </summary>
internal sealed class LegendConnectAutonomousGapPlanner
{
    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _languages;

    public LegendConnectAutonomousGapPlanner(MasterAppDbContext db, ILegendLanguageRegistry languages)
    {
        _db = db;
        _languages = languages;
    }

    public async Task<Guid?> SelectApprovedGapAsync(
        LegendConnectRuntimePolicySnapshot? runtimePolicy = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var candidatesQuery = _db.Set<LegendCorpusCandidate>()
            .Where(item => item.IsApproved &&
                (item.ProcessingState == "Pending" ||
                 (item.ProcessingState == "Processing" && item.LeaseExpiresUtc != null && item.LeaseExpiresUtc < now)));

        // Scope before applying the bounded planner window. Applying this
        // predicate after Take(100) lets an older non-focused backlog hide
        // valid focused work indefinitely, even though both sets use the
        // same canonical candidates and capacity authority.
        if (runtimePolicy?.FocusedTargetLanguageCodes.Count > 0)
        {
            var focusedTargets = runtimePolicy.FocusedTargetLanguageCodes.ToArray();
            candidatesQuery = candidatesQuery.Where(item =>
                item.SourceLanguageCode == "en" &&
                focusedTargets.Contains(item.TargetLanguageCode));
        }

        var candidates = await candidatesQuery
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return null;

        var planned = new List<(LegendCorpusCandidate Candidate, LegendLanguagePairSnapshot Pair)>();
        var deduplicated = false;
        foreach (var candidate in candidates)
        {
            var pair = await _languages.GetOrCreateEnabledPairAsync(
                candidate.SourceLanguageCode,
                candidate.TargetLanguageCode,
                cancellationToken);
            if (pair is null || string.Equals(pair.SourceLanguageCode, pair.TargetLanguageCode, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!MatchesAutonomousLanguageFocus(pair, runtimePolicy))
                continue;

            var source = await _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.LanguageCode == pair.SourceLanguageCode &&
                    item.NormalizedHash == candidate.SourceTextHash, cancellationToken);
            if (source is null || !source.IsTrainingEligible ||
                !string.Equals(source.Text, LegendLanguageIdentity.NormalizeText(candidate.SourceText), StringComparison.Ordinal))
            {
                candidate.IsApproved = false;
                candidate.ProcessingState = "Superseded";
                candidate.ProcessedUtc = DateTime.UtcNow;
                candidate.LeaseExpiresUtc = null;
                candidate.FailureCode = "source_entry_unavailable";
                deduplicated = true;
                continue;
            }
            var alreadyAligned = source is not null && await (
                from alignment in _db.Set<LegendTranslationAlignment>().AsNoTracking()
                join target in _db.Set<LegendLanguageTextUnit>().AsNoTracking()
                    on alignment.TargetTextUnitId equals target.Id
                where alignment.PairKey == pair.PairKey && alignment.SourceTextUnitId == source.Id &&
                    alignment.SupersededUtc == null && target.IsTrainingEligible
                select alignment.Id
            ).AnyAsync(cancellationToken);
            if (alreadyAligned)
            {
                candidate.ProcessingState = "Deduplicated";
                candidate.ProcessedUtc = DateTime.UtcNow;
                candidate.LeaseExpiresUtc = null;
                candidate.FailureCode = null;
                deduplicated = true;
            }
            else
            {
                planned.Add((candidate, pair));
            }
        }
        if (deduplicated)
            await _db.SaveChangesAsync(cancellationToken);
        if (planned.Count == 0)
            return null;

        var pairKeys = planned.Select(item => item.Pair.PairKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var demandByPair = await _db.Set<LegendTranslationPairDemand>()
            .Where(item => pairKeys.Contains(item.PairKey))
            .Select(item => new
            {
                item.PairKey,
                item.TranslationRequestCount,
                item.NeuralModelFailureCount,
                item.ProviderObservationReuseCount,
                item.AzureFallbackCount
            })
            .ToDictionaryAsync(
                item => item.PairKey,
                item =>
                    item.TranslationRequestCount +
                    (item.NeuralModelFailureCount * 4L) +
                    (item.ProviderObservationReuseCount * 2L) +
                    item.AzureFallbackCount,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        return planned
            .OrderByDescending(item => LegendCorpusCandidateScoring.Score(
                item.Candidate,
                demandByPair.GetValueOrDefault(item.Pair.PairKey),
                item.Pair.CorpusCoverage))
            .ThenBy(item => item.Candidate.CreatedUtc)
            .Select(item => (Guid?)item.Candidate.Id)
            .FirstOrDefault();
    }

    private static bool MatchesAutonomousLanguageFocus(
        LegendLanguagePairSnapshot pair,
        LegendConnectRuntimePolicySnapshot? policy)
    {
        // An empty focus leaves the existing demand-and-coverage planner in
        // control. A non-empty focus is the only acquisition scope and is
        // limited to English-source learning material for selected targets.
        return policy is null || policy.FocusedTargetLanguageCodes.Count == 0 ||
               (string.Equals(pair.SourceLanguageCode, "en", StringComparison.OrdinalIgnoreCase) &&
                policy.FocusedTargetLanguageCodes.Contains(pair.TargetLanguageCode, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Disabled-by-default autonomous acquisition authority. The hosted worker
/// invokes it on a cadence; ordinary approved demand gaps never require a
/// developer-triggered job. It shares the live capacity ledger and the same
/// corpus service used by Founder-approved knowledge.
/// </summary>
internal sealed class LegendConnectAutonomousLearningService
{
    private const string MachineConversationProvenance =
        "MachineConversation";
    private const string ExternalObservationProvenance =
        "ExternalObservation";
    private const string LanguageTeacherIssueCategory =
        "LanguageTeacherCircuitIssue";
    private const string LanguageTeacherFailureCategory =
        "LanguageTeacherFailureOccurrence";
    private const string LanguageTeacherRecoveryCategory =
        "LanguageTeacherCircuitRecovery";

    private readonly MasterAppDbContext _db;
    private readonly ILegendLanguageRegistry _registry;
    private readonly ITranslationProvider _provider;
    private readonly ITranslationCapacityAuthority _capacity;
    private readonly LegendConnectCorpusService _corpus;
    private readonly LegendConnectAutonomousGapPlanner _planner;
    private readonly ILegendConnectOperationalEventWriter? _operations;
    private readonly IConfiguration _configuration;
    private readonly ILegendConnectRuntimePolicyAuthority? _runtimePolicy;
    private readonly LegendConnectCurriculumService? _curriculum;
    private readonly ILegendConnectLanguageTeacher? _languageTeacher;

    public LegendConnectAutonomousLearningService(
        MasterAppDbContext db,
        ILegendLanguageRegistry registry,
        ITranslationProvider provider,
        ITranslationCapacityAuthority capacity,
        LegendConnectCorpusService corpus,
        LegendConnectAutonomousGapPlanner planner,
        IConfiguration configuration,
        ILegendConnectOperationalEventWriter? operations = null,
        ILegendConnectRuntimePolicyAuthority? runtimePolicy = null,
        LegendConnectCurriculumService? curriculum = null,
        ILegendConnectLanguageTeacher? languageTeacher = null)
    {
        _db = db;
        _registry = registry;
        _provider = provider;
        _capacity = capacity;
        _corpus = corpus;
        _planner = planner;
        _configuration = configuration;
        _operations = operations;
        _runtimePolicy = runtimePolicy;
        _curriculum = curriculum;
        _languageTeacher = languageTeacher;
    }

    internal bool IsBootstrapEnabled =>
        _configuration.GetValue<bool>("LegendConnect:CorpusAcquisition:Enabled") &&
        (_configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters") ?? 0) > 0;

    /// <summary>
    /// Retains one bounded machine-derived conversational teaching artifact
    /// inside the EXISTING candidate/proposal lifecycle.
    ///
    /// This never creates canonical text, an alignment, curriculum evidence,
    /// training eligibility, Founder approval, or production authority.
    /// </summary>
    internal async Task<LegendConnectMachineTeachingSubmissionResult>
        SubmitConversationMachineProposalAsync(
            LegendConnectMachineTeachingSubmission submission,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (_languageTeacher is null)
        {
            return MachineTeachingFailure(
                "language_critic_unavailable",
                "The existing independent language critic is unavailable.");
        }

        if (_curriculum is null)
        {
            return MachineTeachingFailure(
                "curriculum_authority_unavailable",
                "Machine teaching requires the existing LEGEND curriculum and semantic-transition authority.");
        }

        var capabilityIdentity =
            NormalizeMachineTeachingField(
                submission.CapabilityIdentity,
                40);
        var categoryIdentity =
            NormalizeMachineTeachingField(
                submission.CategoryIdentity,
                40);
        if (categoryIdentity is null ||
            !string.Equals(
                categoryIdentity,
                LegendConnectMachineTeachingSubmission.ReusableSemanticCategory,
                StringComparison.Ordinal))
        {
            return MachineTeachingFailure(
                "machine_teaching_category_not_reusable",
                "Machine teaching accepts only explicitly classified reusable semantic knowledge, never personal or transient facts.");
        }
        if (capabilityIdentity is null ||
            (!string.Equals(
                 capabilityIdentity,
                 LegendConnectMachineTeachingSubmission.TranslationCapability,
                 StringComparison.Ordinal) &&
             !string.Equals(
                 capabilityIdentity,
                 LegendConnectMachineTeachingSubmission.SameLanguageSemanticCapability,
                 StringComparison.Ordinal)))
        {
            return MachineTeachingFailure(
                "machine_teaching_capability_invalid",
                "Machine teaching requires one explicit supported capability identity.");
        }
        if (!LegendConnectCurriculumService
                .HasValidMachineTeachingSemanticTransitions(
                    submission.SemanticTransitions))
        {
            return MachineTeachingFailure(
                "machine_teaching_semantic_transition_invalid",
                "Machine teaching requires one bounded, structurally valid governed semantic transition.");
        }

        var sourceLanguage =
            await _registry.GetEnabledLearningLanguageAsync(
                submission.SourceLanguageCode,
                cancellationToken);
        var targetLanguage =
            await _registry.GetEnabledLearningLanguageAsync(
                submission.TargetLanguageCode,
                cancellationToken);
        if (sourceLanguage is null ||
            targetLanguage is null ||
            !sourceLanguage.IsLearningEnabled ||
            !targetLanguage.IsLearningEnabled)
        {
            return MachineTeachingFailure(
                "machine_teaching_language_unavailable",
                "Machine teaching requires enabled governed learning-language identities.");
        }

        var sameLanguage = string.Equals(
            sourceLanguage.Code,
            targetLanguage.Code,
            StringComparison.OrdinalIgnoreCase);
        if (!LegendConnectMachineTeachingSubmission.IsSupportedIdentity(
                capabilityIdentity,
                categoryIdentity,
                sameLanguage))
        {
            return MachineTeachingFailure(
                "machine_teaching_capability_language_mismatch",
                "Translation requires distinct languages and same-language semantic teaching requires one shared language identity.");
        }

        var pairKey = LegendLanguageIdentity.PairKey(
            sourceLanguage.Code,
            targetLanguage.Code);
        if (!sameLanguage)
        {
            var pair = await _registry.GetOrCreateEnabledPairAsync(
                sourceLanguage.Code,
                targetLanguage.Code,
                cancellationToken);
            if (pair is null)
            {
                return MachineTeachingFailure(
                    "language_pair_unavailable",
                    "Translation teaching requires an enabled directional LEGEND pair.");
            }
            pairKey = pair.PairKey;
        }

        var familyKey =
            NormalizeMachineTeachingField(
                submission.FamilyKey,
                120);

        var semanticCategory =
            NormalizeMachineTeachingField(
                submission.SemanticCategory,
                120);

        var rationale =
            NormalizeMachineTeachingField(
                submission.Rationale,
                1_000);

        if (familyKey is null ||
            familyKey.Length < 3 ||
            semanticCategory is null ||
            rationale is null ||
            submission.Examples is null ||
            submission.Examples.Count is < 2 or > 8)
        {
            return MachineTeachingFailure(
                "machine_teaching_invalid_family",
                "Machine teaching requires one bounded semantic family with 2–8 controlled examples.");
        }

        var researchObservation = submission.ObservationOrigin ==
            LegendConnectMachineObservationOrigin.ExternalResearchObservation;
        if (researchObservation != (submission.ResearchObservationLineage is not null) ||
            (researchObservation && !HasValidResearchObservationLineage(
                submission.ResearchObservationLineage!)))
        {
            return MachineTeachingFailure(
                "machine_teaching_research_lineage_invalid",
                "External research may enter learning only as a complete, citation-validated ExternalObservation lineage.");
        }
        if (researchObservation && !await HasDurableResearchObservationReceiptAsync(
                submission.ResearchObservationLineage!,
                cancellationToken))
        {
            return MachineTeachingFailure(
                "machine_teaching_research_receipt_unavailable",
                "The exact external observation was not found in the existing bounded research observability ledger.");
        }

        var examples =
            new List<LegendLanguageTeacherExampleProposal>(
                submission.Examples.Count);

        var sourceIdentities =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (var submittedExample in submission.Examples)
        {
            var source =
                NormalizeMachineTeachingField(
                    submittedExample.SourceText,
                    2_000);

            var target =
                string.IsNullOrWhiteSpace(
                    submittedExample.TargetText)
                    ? null
                    : NormalizeMachineTeachingField(
                        submittedExample.TargetText,
                        2_000);

            if (source is null ||
                (sameLanguage && target is not null) ||
                !sourceIdentities.Add(
                    LegendLanguageIdentity.TextHash(
                        source)) ||
                submittedExample.Components is null ||
                submittedExample.Components.Count is < 1 or > 16)
            {
                return MachineTeachingFailure(
                    "machine_teaching_invalid_example",
                    sameLanguage && target is not null
                        ? "Same-language semantic teaching uses controlled source examples and cannot masquerade as a same-language translation target."
                        : "Machine teaching examples must be distinct, bounded and component-anchored.");
            }

            var components =
                new List<LegendLanguageTeacherSemanticComponent>(
                    submittedExample.Components.Count);

            var componentIdentities =
                new HashSet<string>(
                    StringComparer.Ordinal);

            foreach (var submittedComponent in
                     submittedExample.Components)
            {
                var dimension =
                    NormalizeMachineTeachingField(
                        submittedComponent.Dimension,
                        80);

                var value =
                    NormalizeMachineTeachingField(
                        submittedComponent.Value,
                        240);

                var surface =
                    NormalizeMachineTeachingField(
                        submittedComponent.SurfaceForm,
                        500);

                if (dimension is null ||
                    value is null ||
                    surface is null)
                {
                    return MachineTeachingFailure(
                        "machine_teaching_invalid_component",
                        "Every machine teaching component requires a bounded dimension, value and observed surface form.");
                }

                var identity =
                    string.Join(
                        "|",
                        dimension.ToLowerInvariant(),
                        value.ToLowerInvariant(),
                        surface.ToLowerInvariant());

                if (!componentIdentities.Add(identity))
                {
                    return MachineTeachingFailure(
                        "machine_teaching_duplicate_component",
                        "A machine teaching example contained duplicate component evidence.");
                }

                components.Add(
                    new LegendLanguageTeacherSemanticComponent(
                        dimension,
                        value,
                        surface));
            }

            examples.Add(
                new LegendLanguageTeacherExampleProposal(
                    source,
                    target,
                    components));
        }

        var family =
            new LegendLanguageTeacherFamilyProposal(
                familyKey,
                semanticCategory,
                rationale,
                Math.Clamp(
                    submission.Confidence,
                    0m,
                    1m),
                examples,
                submission.SemanticTransitions,
                capabilityIdentity,
                categoryIdentity,
                submission.ResearchObservationLineage);

        var initialProposalRequest = researchObservation
            ? BuildResearchObservationProposalRequest(
                sourceLanguage.Code,
                targetLanguage.Code,
                family)
            : sameLanguage
                ? await BuildGovernedSameLanguageMachineProposalRequestAsync(
                sourceLanguage.Code,
                targetLanguage.Code,
                family,
                cancellationToken)
            : null;
        if ((researchObservation || sameLanguage) && initialProposalRequest is null)
        {
            return MachineTeachingFailure(
                researchObservation
                    ? "machine_teaching_research_observation_unproven"
                    : "machine_teaching_same_language_evidence_unproven",
                researchObservation
                    ? "Research retention requires controlled examples that exactly match citation-validated material claims from the observed session."
                    : "Same-language semantic teaching requires controlled examples already resolved by the existing governed meaning authority.");
        }

        var payload =
            JsonSerializer.Serialize(family);

        var candidateKey =
            (researchObservation
                ? "legend-research-observation:v1:"
                : "legend-ai-conversation:v1:") +
            LegendLanguageIdentity.TextHash(
                string.Join(
                    "|",
                    pairKey,
                    payload));

        var candidate =
            await _db.Set<LegendCorpusCandidate>()
                .SingleOrDefaultAsync(
                    item =>
                        item.IdempotencyKey ==
                        candidateKey,
                    cancellationToken);

        var duplicateCandidate =
            candidate is not null;

        if (candidate is null)
        {
            var sourceText =
                examples[0].SourceText;

            candidate =
                new LegendCorpusCandidate
                {
                    Id = Guid.NewGuid(),
                    IdempotencyKey =
                        candidateKey,
                    SourceLanguageCode =
                        sourceLanguage.Code,
                    TargetLanguageCode =
                        targetLanguage.Code,
                    SourceText =
                        sourceText,
                    SourceTextHash =
                        LegendLanguageIdentity.TextHash(
                            sourceText),
                    Category =
                        LegendConnectMachineTeachingSubmission
                            .CandidateCategoryIdentity(
                                capabilityIdentity,
                                categoryIdentity),
                    Provenance =
                        researchObservation
                            ? ExternalObservationProvenance
                            : MachineConversationProvenance,

                    // CRITICAL:
                    // conversation-derived material never impersonates an
                    // approved Azure acquisition candidate.
                    IsApproved = false,

                    Priority = 0,
                    ProcessingState =
                        "ConversationProposal",
                    TeacherProposalProcessingState =
                        "Pending",
                    CreatedUtc =
                        DateTime.UtcNow
                };

            _db.Set<LegendCorpusCandidate>()
                .Add(candidate);

            try
            {
                await _db.SaveChangesAsync(
                    cancellationToken);
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();

                candidate =
                    await _db.Set<LegendCorpusCandidate>()
                        .SingleAsync(
                            item =>
                                item.IdempotencyKey ==
                                candidateKey,
                            cancellationToken);

                duplicateCandidate = true;
            }
        }

        if (!string.Equals(
                candidate.Provenance,
                researchObservation
                    ? ExternalObservationProvenance
                    : MachineConversationProvenance,
                StringComparison.Ordinal) ||
            candidate.IsApproved ||
            !string.Equals(
                candidate.ProcessingState,
                "ConversationProposal",
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.SourceLanguageCode,
                sourceLanguage.Code,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidate.TargetLanguageCode,
                targetLanguage.Code,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidate.Category,
                LegendConnectMachineTeachingSubmission.CandidateCategoryIdentity(
                    capabilityIdentity,
                    categoryIdentity),
                StringComparison.Ordinal))
        {
            return MachineTeachingFailure(
                "machine_teaching_identity_collision",
                "The deterministic candidate identity belongs to incompatible existing work.");
        }

        var requestBuild = initialProposalRequest is not null
            ? LanguageProposalRequestBuildResult.Accepted(
                initialProposalRequest)
            : await BuildLanguageProposalRequestAsync(
                candidate,
                cancellationToken,
                family);
        var request = requestBuild.Request;
        var insufficientEvidenceCode = requestBuild.FailureCode ??
            "conversation_machine_relevant_evidence_insufficient";

        var evidenceIdentityHash =
            request is null
                ? LegendLanguageIdentity.TextHash(
                    "insufficient|" +
                    candidate.IdempotencyKey + "|" +
                    insufficientEvidenceCode)
                : LegendLanguageIdentity.TextHash(
                    string.Join(
                        "\n",
                        request.Evidence
                            .Select(
                                item =>
                                    item.EvidenceIdentity)
                            .OrderBy(
                                item => item,
                                StringComparer.Ordinal)));

        var proposalIdentity =
            LegendLanguageIdentity.TextHash(
                string.Join(
                    "|",
                    "language-teacher-proposal:v1",
                    candidate.IdempotencyKey,
                    evidenceIdentityHash,
                    payload));

        var existing =
            await _db.Set<LegendLanguageTeacherProposal>()
                .SingleOrDefaultAsync(
                    item =>
                        item.ProposalIdentity ==
                        proposalIdentity,
                    cancellationToken);

        if (existing is not null)
        {
            return new LegendConnectMachineTeachingSubmissionResult(
                true,
                true,
                existing.ValidationState,
                null,
                researchObservation
                    ? "The exact external observation proposal is already retained in LEGEND."
                    : "The exact conversational teaching artifact is already retained in LEGEND.",
                candidate.Id,
                existing.Id,
                ProposalAlreadyExisted: true);
        }

        var state =
            request is null
                ? "InsufficientEvidence"
                : "AwaitingCritic";

        var now =
            DateTime.UtcNow;

        var proposal =
            new LegendLanguageTeacherProposal
            {
                Id = Guid.NewGuid(),
                CorpusCandidateId =
                    candidate.Id,
                ProposalIdentity =
                    proposalIdentity,
                PairKey =
                    pairKey,
                SourceLanguageCode =
                    sourceLanguage.Code,
                TargetLanguageCode =
                    targetLanguage.Code,
                EvidenceIdentityHash =
                    evidenceIdentityHash,
                FamilyKey =
                    family.FamilyKey,
                SemanticCategory =
                    family.SemanticCategory,
                Rationale =
                    family.Rationale,
                Confidence =
                    family.Confidence,
                ProposalPayloadJson =
                    payload,
                CriticApproved =
                    false,
                CriticConfidence =
                    null,
                CriticReasonCodesJson =
                    "[]",
                ValidationState =
                    state,
                Provenance =
                    "MachineProposed",
                CreatedUtc =
                    now,
                UpdatedUtc =
                    now
            };

        _db.Set<LegendLanguageTeacherProposal>()
            .Add(proposal);

        candidate.TeacherProposalProcessingState =
            request is null
                ? "InsufficientEvidence"
                : "Pending";

        candidate.TeacherProposalFailureCode =
            request is null
                ? insufficientEvidenceCode
                : null;

        candidate.TeacherProposalLeaseExpiresUtc =
            null;

        candidate.TeacherProposalProcessedUtc =
            request is null
                ? now
                : null;

        try
        {
            await _db.SaveChangesAsync(
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();

            var concurrent =
                await _db.Set<LegendLanguageTeacherProposal>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        item =>
                            item.ProposalIdentity ==
                            proposalIdentity,
                        cancellationToken);

            if (concurrent is null)
                throw;

            return new LegendConnectMachineTeachingSubmissionResult(
                true,
                true,
                concurrent.ValidationState,
                null,
                researchObservation
                    ? "Concurrent external-observation retention converged on the existing deterministic LEGEND proposal."
                    : "Concurrent conversational teaching converged on the existing deterministic LEGEND proposal.",
                candidate.Id,
                concurrent.Id,
                ProposalAlreadyExisted: true);
        }

        await RecordAsync(
            researchObservation
                ? "ResearchExternalObservationProposal"
                : "ConversationMachineProposal",
            "Info",
            state,
            sourceLanguage.Code,
            pairKey,
            request is null
                ? insufficientEvidenceCode
                : null,
            request is null
                ? researchObservation
                    ? "The external observation was retained as MachineProposed but lacks sufficient governed evidence to enter critique."
                    : "The conversational teaching artifact was retained as MachineProposed but lacks sufficient governed evidence to enter critique."
                : researchObservation
                    ? "The external observation entered the existing MachineProposed teacher/critic lifecycle without gaining canonical or serving authority."
                    : "The conversational teaching artifact was retained as MachineProposed and queued on the existing teacher/critic lifecycle.",
            cancellationToken);

        return new LegendConnectMachineTeachingSubmissionResult(
            true,
            duplicateCandidate,
            state,
            request is null
                ? insufficientEvidenceCode
                : null,
            request is null
                ? "LEGEND retained the proposal but will not promote it without additional governed evidence."
                : "LEGEND retained the proposal and the existing independent critic will examine it.",
            candidate.Id,
            proposal.Id);
    }

    private static bool IsConversationMachineCandidate(
        LegendCorpusCandidate candidate) =>
        !candidate.IsApproved &&
        candidate.Provenance is
            MachineConversationProvenance or ExternalObservationProvenance &&
        string.Equals(
            candidate.ProcessingState,
            "ConversationProposal",
            StringComparison.Ordinal);

    internal static bool HasValidResearchObservationLineage(
        LegendConnectResearchRetentionLineage lineage) =>
        LegendConnectResearchRetentionContracts.IsStructurallyValid(lineage);

    private static LegendLanguageTeacherProposalRequest?
        BuildResearchObservationProposalRequest(
            string sourceLanguageCode,
            string targetLanguageCode,
            LegendLanguageTeacherFamilyProposal family)
    {
        var lineage = family.ResearchObservationLineage;
        if (lineage is null || !HasValidResearchObservationLineage(lineage))
            return null;

        var materialByStatement = lineage.MaterialClaims
            .GroupBy(item => LegendLanguageIdentity.NormalizeText(item.Statement), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var evidence = new List<LegendLanguageTeacherEvidence>(family.Examples.Count);
        foreach (var example in family.Examples)
        {
            var statement = LegendLanguageIdentity.NormalizeText(example.SourceText);
            if (!materialByStatement.TryGetValue(statement, out var material))
                return null;
            evidence.Add(new LegendLanguageTeacherEvidence(
                material.EvidenceIdentity,
                statement,
                example.TargetText,
                ExternalObservationProvenance,
                material.VerificationState.ToString()));
        }

        if (evidence.Count != lineage.MaterialClaims.Count ||
            evidence.Select(item => item.EvidenceIdentity)
                .Distinct(StringComparer.Ordinal).Count() != evidence.Count)
        {
            return null;
        }

        return new LegendLanguageTeacherProposalRequest(
            sourceLanguageCode,
            targetLanguageCode,
            family.SemanticCategory,
            evidence,
            MaximumFamilies: 1,
            CapabilityIdentity: family.CapabilityIdentity,
            CategoryIdentity: family.CategoryIdentity,
            SemanticFamilyKey: family.FamilyKey,
            SemanticCategory: family.SemanticCategory);
    }

    private async Task<bool> HasDurableResearchObservationReceiptAsync(
        LegendConnectResearchRetentionLineage lineage,
        CancellationToken cancellationToken)
    {
        var correlation = lineage.SessionId.ToString("N");
        var events = await _db.Set<LegendConnectOperationalEvent>()
            .AsNoTracking()
            .Where(item =>
                item.Category == LegendConnectResearchContracts.ObservabilityCategory &&
                item.CorrelationId == correlation)
            .Select(item => new
            {
                item.Status,
                item.ErrorCode,
                item.Summary
            })
            .ToListAsync(cancellationToken);
        if (!events.Any(item =>
                item.Status == "Session:Conclusion" &&
                item.ErrorCode == null &&
                (item.Summary ?? string.Empty).Contains(
                    "code_sha=" + lineage.CodeSha,
                    StringComparison.Ordinal) &&
                (item.Summary ?? string.Empty).Contains(
                    "configuration=" + lineage.ConfigurationIdentity,
                    StringComparison.Ordinal)) ||
            !events.Any(item =>
                item.Status == "Retention:ExternalObservation" &&
                (item.Summary ?? string.Empty).Contains(
                    "observation=" + lineage.ObservationIdentity,
                    StringComparison.Ordinal) &&
                (item.Summary ?? string.Empty).Contains(
                    "provenance=ExternalObservation",
                    StringComparison.Ordinal)))
        {
            return false;
        }

        return lineage.MaterialClaims.All(claim =>
                   events.Any(item =>
                       (item.Status is "Claim:Supported" or "Claim:Contradicted") &&
                       (item.Summary ?? string.Empty).Contains(
                           "evidence=" + claim.EvidenceIdentity,
                           StringComparison.Ordinal))) &&
               lineage.MaterialClaims
                   .Select(item => item.SourceIdentity)
                   .Distinct(StringComparer.Ordinal)
                   .All(source => events.Any(item =>
                       (item.Status is "Source:Opened" or "Source:Discovered") &&
                       (item.Summary ?? string.Empty).Contains(
                           "source=" + source,
                           StringComparison.Ordinal))) &&
               lineage.Citations.All(citation => events.Any(item =>
                   item.Status == "Citation:Used" &&
                   (item.Summary ?? string.Empty).Contains(
                       "citation=" + citation.CitationIdentity,
                       StringComparison.Ordinal)));
    }

    private static string? NormalizeMachineTeachingField(
        string? value,
        int maximumLength)
    {
        var normalized =
            LegendLanguageIdentity.NormalizeText(
                value ?? string.Empty);

        if (string.IsNullOrWhiteSpace(
                normalized) ||
            normalized.Length >
                maximumLength)
        {
            return null;
        }

        return normalized;
    }

    private static LegendConnectMachineTeachingSubmissionResult
        MachineTeachingFailure(
            string errorCode,
            string message) =>
        new(
            false,
            false,
            "Rejected",
            errorCode,
            message,
            null,
            null);

    public async Task ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        LegendConnectRuntimePolicySnapshot? runtime = null;
        if (_runtimePolicy is not null)
        {
            runtime = await _runtimePolicy.GetEffectiveAsync(cancellationToken);
            if (!runtime.CorpusAcquisitionEnabled || !runtime.LearningEnabled)
                return;
            var readiness = await _runtimePolicy.GetReadinessAsync(cancellationToken);
            if (readiness.State is "BLOCKED" or "DEGRADED")
                return;
        }
        else if (!IsBootstrapEnabled)
            return;

        // Phase 5 consumes already-SystemValidated proposals through the
        // existing curriculum/corpus authorities before creating additional
        // proposal work. No second worker or admission queue exists.
        if (_curriculum is not null &&
            await _curriculum
                .ProcessOneSystemValidatedMachineProposalAsync(
                    cancellationToken))
        {
            return;
        }

        // Phase 4 remains inside this existing autonomous authority and hosted
        // cadence. Canonical validation is processed before new teacher/provider
        // work so already-created machine proposals cannot accumulate behind
        // additional acquisition. It does not require the external teacher.
        if (_curriculum is not null &&
            await TryProcessPendingCanonicalLanguageProposalAsync(cancellationToken))
        {
            return;
        }

        // Phase 3 reuses this exact autonomous authority and hosted cadence.
        // Proposal retries are processed before selecting new acquisition work,
        // but an unavailable teacher never rolls back or blocks an already
        // completed Azure/corpus acquisition.
        if (_languageTeacher is not null &&
            await TryProcessPendingLanguageProposalAsync(cancellationToken))
        {
            return;
        }

        var candidateId = await _planner.SelectApprovedGapAsync(runtime, cancellationToken);
        if (candidateId is null)
            return;
        var candidate = await TryClaimCandidateAsync(candidateId.Value, cancellationToken);
        if (candidate is null)
            return;

        var pair = await _registry.GetOrCreateEnabledPairAsync(
            candidate.SourceLanguageCode,
            candidate.TargetLanguageCode,
            cancellationToken);
        if (pair is null || string.Equals(pair.SourceLanguageCode, pair.TargetLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            candidate.ProcessingState = "Rejected";
            candidate.FailureCode = "language_pair_unavailable";
            candidate.ProcessedUtc = DateTime.UtcNow;
            candidate.LeaseExpiresUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            await RecordAsync("PairProvisioning", "Error", "Rejected", candidate.SourceLanguageCode, candidate.PairKey(), "language_pair_unavailable", "The approved autonomous candidate could not resolve an enabled pair.", cancellationToken);
            return;
        }

        await RecordAsync(
            "CorpusCandidate",
            "Info",
            "Selected",
            pair.SourceLanguageCode,
            pair.PairKey,
            null,
            "An approved, provenance-tagged autonomous candidate was selected by the existing planner.",
            cancellationToken);

        var source = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.LanguageCode == pair.SourceLanguageCode &&
                item.NormalizedHash == candidate.SourceTextHash, cancellationToken);
        if (source is null || !source.IsTrainingEligible ||
            !string.Equals(source.Text, LegendLanguageIdentity.NormalizeText(candidate.SourceText), StringComparison.Ordinal))
        {
            candidate.IsApproved = false;
            candidate.ProcessingState = "Superseded";
            candidate.FailureCode = "source_entry_unavailable";
            candidate.ProcessedUtc = DateTime.UtcNow;
            candidate.LeaseExpiresUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            await RecordAsync("CorpusCandidate", "Info", "Superseded", pair.SourceLanguageCode, pair.PairKey,
                "source_entry_unavailable", "A retired source asset was excluded before provider acquisition.", cancellationToken);
            return;
        }
        var alreadyAligned = source is not null && await (
            from alignment in _db.Set<LegendTranslationAlignment>()
            join target in _db.Set<LegendLanguageTextUnit>() on alignment.TargetTextUnitId equals target.Id
            where alignment.PairKey == pair.PairKey && alignment.SourceTextUnitId == source.Id &&
                alignment.SupersededUtc == null && target.IsTrainingEligible
            select alignment.Id
        ).AnyAsync(cancellationToken);
        if (alreadyAligned)
        {
            candidate.ProcessingState = "Deduplicated";
            candidate.ProcessedUtc = DateTime.UtcNow;
            candidate.LeaseExpiresUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            await RecordAsync("DuplicatePrevention", "Info", "Prevented", pair.SourceLanguageCode, pair.PairKey, "canonical_alignment_exists", "An approved autonomous candidate matched an existing canonical directional alignment.", cancellationToken);
            return;
        }

        var reservation = await _capacity.TryReserveAsync(
            _provider.ProviderName,
            candidate.SourceText.Length,
            TranslationCapacityPurpose.Bootstrap,
            reservationReference: candidate.IdempotencyKey,
            cancellationToken: cancellationToken);
        if (reservation is null)
        {
            await DeferCandidateAsync(candidate, "translation_capacity_unavailable", cancellationToken);
            await RecordAsync("CapacityReservation", "Warning", "Unavailable", pair.SourceLanguageCode, pair.PairKey, "translation_capacity_unavailable", "Autonomous acquisition deferred because live-reserved provider capacity was unavailable.", cancellationToken);
            return;
        }

        await RecordAsync(
            "CapacityReservation",
            "Info",
            "Reserved",
            pair.SourceLanguageCode,
            pair.PairKey,
            null,
            $"{reservation.Characters:N0} provider character(s) were reserved outside the protected live reserve.",
            cancellationToken);

        var providerSucceeded = false;
        var providerExecuted = false;
        try
        {
            providerExecuted = true;
            var translation = await _provider.TranslateAsync(
                candidate.SourceText,
                pair.TargetLanguageCode,
                pair.SourceLanguageCode,
                cancellationToken);
            providerSucceeded = translation.Succeeded && !string.IsNullOrWhiteSpace(translation.TranslatedText);
            if (!providerSucceeded)
            {
                await DeferCandidateAsync(candidate, translation.ErrorCode ?? "translation_provider_failed", cancellationToken);
                await RecordAsync("AzureProvider", "Error", "Failed", pair.SourceLanguageCode, pair.PairKey, candidate.FailureCode, "Azure did not return a usable autonomous acquisition result.", cancellationToken);
                return;
            }

            await RecordAsync(
                "AzureProvider",
                "Info",
                "Succeeded",
                pair.SourceLanguageCode,
                pair.PairKey,
                null,
                "Azure returned a candidate result for existing validation and corpus processing.",
                cancellationToken);

            var item = new LegendTranslationLearningEvent
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = "corpus:" + candidate.IdempotencyKey,
                SourceLanguageCode = pair.SourceLanguageCode,
                TargetLanguageCode = pair.TargetLanguageCode,
                PairKey = pair.PairKey,
                SourceTextHash = LegendLanguageIdentity.TextHash(candidate.SourceText),
                TargetTextHash = LegendLanguageIdentity.TextHash(translation.TranslatedText!),
                SourceText = candidate.SourceText,
                TargetText = translation.TranslatedText,
                Provider = translation.Provider,
                // The candidate retains the Founder-approved source
                // provenance. The Azure result is a distinct target asset and
                // must not inherit approval or verification from that source.
                Provenance = LegendConnectKnowledgeProvenance.ProviderDerived,
                ContextCategory = candidate.Category,
                EligibilityState = "Eligible",
                ProcessingState = "Pending",
                CreatedUtc = DateTime.UtcNow
            };
            _db.Set<LegendTranslationLearningEvent>().Add(item);
            candidate.ProcessingState = "Queued";
            candidate.ProcessedUtc = DateTime.UtcNow;
            candidate.LeaseExpiresUtc = null;
            candidate.ProviderCharactersConsumed = reservation.Characters;
            await _db.SaveChangesAsync(cancellationToken);
            await _corpus.ProcessAsync(item, cancellationToken);
            if (_curriculum is not null)
            {
                try
                {
                    await _curriculum.AttachProcessedExpansionAsync(candidate, pair, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Corpus promotion is already durable and must not be
                    // rolled back by optional structural analysis.
                    await RecordAsync(
                        "StructuralLearning",
                        "Warning",
                        "Deferred",
                        pair.TargetLanguageCode,
                        pair.PairKey,
                        "structural_analysis_failed",
                        "Curriculum structural analysis was isolated after canonical corpus processing.",
                        CancellationToken.None);
                    _ = exception;
                }
            }

            // Only a successfully processed existing corpus observation opens
            // Phase-3 proposal work. Historical Queued candidates are not
            // silently replayed in this milestone.
            if (item.ProcessingState == "Processed" &&
                candidate.TeacherProposalProcessingState == "NotStarted")
            {
                candidate.TeacherProposalProcessingState = "Pending";
                candidate.TeacherProposalFailureCode = null;
                candidate.TeacherProposalLeaseExpiresUtc = null;
                candidate.TeacherProposalProcessedUtc = null;
                await _db.SaveChangesAsync(cancellationToken);
            }

            await RecordAsync(
                "CorpusExpansion",
                "Info",
                "Processed",
                pair.SourceLanguageCode,
                pair.PairKey,
                null,
                "The existing corpus pipeline validated and processed the autonomous result.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await DeferCandidateAsync(candidate, "translation_provider_failed", CancellationToken.None);
            await RecordAsync(
                "AzureProvider",
                "Error",
                "Failed",
                pair.SourceLanguageCode,
                pair.PairKey,
                candidate.FailureCode,
                "The provider path interrupted before the autonomous candidate could be committed; retry is lease-delayed.",
                CancellationToken.None);
        }
        finally
        {
            // Preserve the reservation after an attempted Azure call even
            // when its result is unavailable; Azure can have accepted the
            // input before a timeout or transport failure reaches us.
            await _capacity.CompleteAsync(reservation, providerExecuted, cancellationToken);
        }
    }

    private async Task<bool> TryProcessPendingCanonicalLanguageProposalAsync(
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;

        var proposal = await TryClaimCanonicalLanguageProposalAsync(
            maximumAttempts,
            cancellationToken);

        if (proposal is null)
            return false;

        try
        {
            var candidate = await _db.Set<LegendCorpusCandidate>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == proposal.CorpusCandidateId,
                    cancellationToken);

            if (candidate is null)
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_candidate_missing",
                    cancellationToken);
                return true;
            }

            if (!proposal.CriticApproved ||
                !string.Equals(
                    proposal.Provenance,
                    "MachineProposed",
                    StringComparison.Ordinal))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_proposal_not_critic_approved",
                    cancellationToken);
                return true;
            }

            var expectedPairKey = LegendLanguageIdentity.PairKey(
                candidate.SourceLanguageCode,
                candidate.TargetLanguageCode);

            if (!string.Equals(
                    proposal.PairKey,
                    expectedPairKey,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    proposal.SourceLanguageCode,
                    candidate.SourceLanguageCode,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    proposal.TargetLanguageCode,
                    candidate.TargetLanguageCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_language_lineage_mismatch",
                    cancellationToken);
                return true;
            }

            LegendLanguageTeacherFamilyProposal? family;
            try
            {
                family =
                    JsonSerializer.Deserialize<LegendLanguageTeacherFamilyProposal>(
                        proposal.ProposalPayloadJson);
            }
            catch (JsonException)
            {
                family = null;
            }

            if (family is null ||
                family.Examples is null ||
                family.Examples.Count is < 2 or > 8 ||
                string.IsNullOrWhiteSpace(family.FamilyKey) ||
                string.IsNullOrWhiteSpace(family.SemanticCategory) ||
                !string.Equals(
                    family.FamilyKey,
                    proposal.FamilyKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    family.SemanticCategory,
                    proposal.SemanticCategory,
                    StringComparison.Ordinal))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_proposal_payload_invalid",
                    cancellationToken);
                return true;
            }

            if (IsConversationMachineCandidate(candidate) &&
                (family.SemanticTransitions is null || family.SemanticTransitions.Count == 0))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_conversation_transition_missing",
                    cancellationToken);
                return true;
            }

            // Reuse the exact Phase-3 evidence construction. Phase 4 does not
            // introduce a second evidence query or reinterpret what the teacher
            // originally saw.
            var requestBuild = await BuildLanguageProposalRequestAsync(
                candidate,
                cancellationToken,
                family);
            var request = requestBuild.Request;

            if (request is null)
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "InsufficientEvidence",
                    requestBuild.FailureCode ??
                        "canonical_governed_evidence_unavailable",
                    cancellationToken);
                return true;
            }

            var currentEvidenceIdentityHash =
                LegendLanguageIdentity.TextHash(
                    string.Join(
                        "\n",
                        request.Evidence
                            .Select(item => item.EvidenceIdentity)
                            .OrderBy(item => item, StringComparer.Ordinal)));

            if (!string.Equals(
                    proposal.EvidenceIdentityHash,
                    currentEvidenceIdentityHash,
                    StringComparison.Ordinal))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_evidence_identity_mismatch",
                    cancellationToken);
                return true;
            }

            var sameLanguage = string.Equals(
                proposal.SourceLanguageCode,
                proposal.TargetLanguageCode,
                StringComparison.OrdinalIgnoreCase);
            if (!LegendConnectMachineTeachingSubmission.IsSupportedIdentity(
                    family.CapabilityIdentity,
                    family.CategoryIdentity,
                    sameLanguage) ||
                (IsConversationMachineCandidate(candidate) &&
                 !string.Equals(
                     candidate.Category,
                     LegendConnectMachineTeachingSubmission.CandidateCategoryIdentity(
                         family.CapabilityIdentity,
                         family.CategoryIdentity),
                     StringComparison.Ordinal)))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_capability_identity_mismatch",
                    cancellationToken);
                return true;
            }

            // Recompute the exact Phase-3 proposal identity from the original
            // candidate, current exact governed evidence identity, and exact
            // persisted payload. Changed lineage can never inherit an earlier
            // critic decision.
            var expectedProposalIdentity =
                LegendLanguageIdentity.TextHash(
                    string.Join(
                        "|",
                        "language-teacher-proposal:v1",
                        candidate.IdempotencyKey,
                        currentEvidenceIdentityHash,
                        proposal.ProposalPayloadJson));

            if (!string.Equals(
                    proposal.ProposalIdentity,
                    expectedProposalIdentity,
                    StringComparison.Ordinal))
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    "Rejected",
                    "canonical_proposal_identity_mismatch",
                    cancellationToken);
                return true;
            }

            var canonicalValidation =
                await ValidateCanonicalMachineProposalAsync(
                    candidate,
                    proposal,
                    family,
                    cancellationToken);
            if (!canonicalValidation.Succeeded)
            {
                await CompleteCanonicalLanguageProposalAsync(
                    proposal,
                    canonicalValidation.State,
                    canonicalValidation.FailureCode!,
                    cancellationToken);
                return true;
            }

            // Phase 4 admits only the proposal artifact into the governed
            // machine-validation tier. It deliberately creates no curriculum
            // family, example, alignment, text unit, structural evidence, or
            // expansion candidate. Phase 5 owns that boundary.
            proposal.ValidationState = "SystemValidated";
            proposal.Provenance =
                LegendConnectKnowledgeProvenance.SystemValidatedMachine;
            proposal.CanonicalValidationFailureCode = null;
            proposal.CanonicalValidationLeaseExpiresUtc = null;
            proposal.CanonicalValidatedUtc = DateTime.UtcNow;
            proposal.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);

            await RecordAsync(
                "CanonicalLanguageProposal",
                "Info",
                "SystemValidated",
                proposal.SourceLanguageCode,
                proposal.PairKey,
                null,
                "The critic-approved machine proposal passed governed definition, constraint, contradiction, family, contrast, transition, and held-out validation. No curriculum or corpus knowledge was written.",
                cancellationToken);

            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            proposal.CanonicalValidationFailureCode =
                "canonical_validation_failed";

            if (proposal.CanonicalValidationAttemptCount >= maximumAttempts)
            {
                proposal.ValidationState = "Failed";
                proposal.CanonicalValidationLeaseExpiresUtc = null;
                proposal.CanonicalValidatedUtc = DateTime.UtcNow;
            }
            else
            {
                proposal.ValidationState =
                    "CanonicalValidationProcessing";
                proposal.CanonicalValidationLeaseExpiresUtc =
                    DateTime.UtcNow.AddMinutes(
                        Math.Min(
                            30,
                            Math.Max(
                                5,
                                proposal.CanonicalValidationAttemptCount * 5)));
            }

            proposal.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(CancellationToken.None);

            await RecordAsync(
                "CanonicalLanguageProposal",
                "Warning",
                proposal.ValidationState,
                proposal.SourceLanguageCode,
                proposal.PairKey,
                proposal.CanonicalValidationFailureCode,
                "Canonical proposal validation failed closed and did not mutate corpus or curriculum knowledge.",
                CancellationToken.None);

            return true;
        }
    }

    private async Task<CanonicalMachineProposalValidation>
        ValidateCanonicalMachineProposalAsync(
            LegendCorpusCandidate candidate,
            LegendLanguageTeacherProposal proposal,
            LegendLanguageTeacherFamilyProposal family,
            CancellationToken cancellationToken)
    {
        var proposedLineage = LegendConnectCurriculumService
            .NormalizeMachineTeachingSemanticLineage(family);
        if (proposedLineage is null)
        {
            return CanonicalMachineProposalValidation.Rejected(
                "canonical_semantic_lineage_invalid");
        }

        LegendCurriculumFamily? governedFamily;
        if (candidate.CurriculumFamilyId is Guid familyId)
        {
            governedFamily = await _db.Set<LegendCurriculumFamily>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == familyId,
                    cancellationToken);
        }
        else
        {
            var matches = await _db.Set<LegendCurriculumFamily>()
                .AsNoTracking()
                .Where(item =>
                    item.FamilyKey == proposedLineage.FamilyKey)
                .Take(2)
                .ToListAsync(cancellationToken);
            governedFamily = matches.Count == 1
                ? matches[0]
                : null;
        }

        var governedCategory = governedFamily is null ||
            string.IsNullOrWhiteSpace(governedFamily.SemanticCategory)
                ? governedFamily?.FamilyKey
                : governedFamily.SemanticCategory.Trim();
        if (governedFamily is null ||
            !string.Equals(
                governedFamily.Provenance,
                LegendConnectKnowledgeProvenance.FounderApproved,
                StringComparison.Ordinal) ||
            !string.Equals(
                governedFamily.FamilyKey,
                proposedLineage.FamilyKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                governedCategory,
                proposedLineage.SemanticCategory,
                StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalMachineProposalValidation.Rejected(
                "canonical_founder_family_lineage_unproven");
        }

        var governedExamples = await _db
            .Set<LegendCurriculumExample>()
            .AsNoTracking()
            .Where(item =>
                item.CurriculumFamilyId == governedFamily.Id &&
                item.LanguageCode == proposal.SourceLanguageCode &&
                item.SupersededUtc == null &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (governedExamples.Count < 2)
        {
            return CanonicalMachineProposalValidation.Insufficient(
                "canonical_founder_family_examples_insufficient");
        }

        var governedAnchors = await _db
            .Set<LegendLanguageCompositionalAnchor>()
            .AsNoTracking()
            .Where(item =>
                governedExamples.Contains(item.CurriculumExampleId) &&
                item.CurriculumFamilyId == governedFamily.Id &&
                item.LanguageCode == proposal.SourceLanguageCode &&
                item.SupersededUtc == null &&
                item.SemanticSignature != null &&
                item.SemanticSignature != string.Empty &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new
            {
                item.CurriculumExampleId,
                item.Dimension,
                item.Value,
                SemanticSignature = item.SemanticSignature!
            })
            .Distinct()
            .ToListAsync(cancellationToken);
        if (governedAnchors.Count == 0)
        {
            return CanonicalMachineProposalValidation.Insufficient(
                "canonical_founder_definitions_insufficient");
        }

        var definitions = governedAnchors
            .GroupBy(item => item.SemanticSignature, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => CanonicalMachineDefinitionIdentity(
                        item.Dimension,
                        item.Value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        if (definitions.Values.Any(item => item.Length != 1))
        {
            return CanonicalMachineProposalValidation.Rejected(
                "canonical_founder_definition_contradicted");
        }

        var governedProfiles = governedAnchors
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => CanonicalMachineProfile(
                    group.Select(item => item.SemanticSignature)));
        var governedProfileSet = governedProfiles.Values
            .ToHashSet(StringComparer.Ordinal);

        var contrastRows = await _db
            .Set<LegendLanguageStructuralEvidence>()
            .AsNoTracking()
            .Where(item =>
                item.CurriculumFamilyId == governedFamily.Id &&
                item.LanguageCode == proposal.SourceLanguageCode &&
                item.PairKey == string.Empty &&
                governedExamples.Contains(
                    item.BaselineCurriculumExampleId) &&
                governedExamples.Contains(
                    item.ComparedCurriculumExampleId) &&
                item.SupersededUtc == null &&
                item.IsHumanVerifiedSupport &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new
            {
                item.BaselineCurriculumExampleId,
                item.ComparedCurriculumExampleId,
                item.ContributionState
            })
            .ToListAsync(cancellationToken);

        var supportedContrasts = new HashSet<string>(
            StringComparer.Ordinal);
        var contradictedContrasts = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var contrast in contrastRows)
        {
            if (!governedProfiles.TryGetValue(
                    contrast.BaselineCurriculumExampleId,
                    out var baselineProfile) ||
                !governedProfiles.TryGetValue(
                    contrast.ComparedCurriculumExampleId,
                    out var comparedProfile))
            {
                continue;
            }

            var contrastIdentity = CanonicalMachineContrastIdentity(
                baselineProfile,
                comparedProfile);
            if (string.Equals(
                    contrast.ContributionState,
                    "Supported",
                    StringComparison.Ordinal))
            {
                supportedContrasts.Add(contrastIdentity);
            }
            else if (string.Equals(
                         contrast.ContributionState,
                         "Contradictory",
                         StringComparison.Ordinal))
            {
                contradictedContrasts.Add(contrastIdentity);
            }
        }

        var proposedProfiles = proposedLineage.Examples
            .Select(item => CanonicalMachineProfile(
                item.PrimitiveSignatures))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (proposedProfiles.Length < 2)
        {
            return CanonicalMachineProposalValidation.Rejected(
                "canonical_controlled_contrast_missing");
        }
        if (proposedProfiles.Any(
                item => !governedProfileSet.Contains(item)))
        {
            return CanonicalMachineProposalValidation.Rejected(
                "canonical_semantic_profile_outside_founder_family");
        }

        foreach (var profile in proposedProfiles)
        {
            var peerIdentities = proposedProfiles
                .Where(item => !string.Equals(
                    item,
                    profile,
                    StringComparison.Ordinal))
                .Select(item => CanonicalMachineContrastIdentity(
                    profile,
                    item))
                .ToArray();
            if (peerIdentities.Any(
                    contradictedContrasts.Contains))
            {
                return CanonicalMachineProposalValidation.Rejected(
                    "canonical_controlled_contrast_contradicted");
            }
            if (!peerIdentities.Any(supportedContrasts.Contains))
            {
                return CanonicalMachineProposalValidation.Insufficient(
                    "canonical_controlled_contrast_insufficient");
            }
        }

        if (proposedLineage.TransitionSignatures.Count > 0)
        {
            var proposedTransitionSignatures =
                proposedLineage.TransitionSignatures.ToArray();
            var transitionStates = await _db
                .Set<LegendSemanticTransitionEvidence>()
                .AsNoTracking()
                .Where(item =>
                    governedExamples.Contains(
                        item.SourceCurriculumExampleId) &&
                    governedExamples.Contains(
                        item.ResultCurriculumExampleId) &&
                    item.SourceLanguageCode ==
                        proposal.SourceLanguageCode &&
                    item.ResultLanguageCode ==
                        proposal.SourceLanguageCode &&
                    proposedTransitionSignatures.Contains(
                        item.TransitionSignature) &&
                    item.SupersededUtc == null &&
                    item.IsHumanVerifiedSupport &&
                    item.Provenance ==
                        LegendConnectKnowledgeProvenance.FounderApproved)
                .Select(item => new
                {
                    item.TransitionSignature,
                    item.ContributionState
                })
                .ToListAsync(cancellationToken);
            if (transitionStates.Any(item => string.Equals(
                    item.ContributionState,
                    "Contradictory",
                    StringComparison.Ordinal)))
            {
                return CanonicalMachineProposalValidation.Rejected(
                    "canonical_semantic_transition_contradicted");
            }

            var supportedTransitions = transitionStates
                .Where(item => string.Equals(
                    item.ContributionState,
                    "Supported",
                    StringComparison.Ordinal))
                .Select(item => item.TransitionSignature)
                .ToHashSet(StringComparer.Ordinal);
            if (proposedTransitionSignatures.Any(
                    item => !supportedTransitions.Contains(item)))
            {
                return CanonicalMachineProposalValidation.Insufficient(
                    "canonical_semantic_transition_unsupported");
            }
        }

        var containsNovelBehavior = false;
        foreach (var example in family.Examples)
        {
            var normalized = LegendConnectCurriculumService
                .NormalizeMachineTeachingExampleSemantics(example);
            if (normalized is null)
            {
                return CanonicalMachineProposalValidation.Rejected(
                    "canonical_example_constraints_invalid");
            }

            var normalizedProfile = CanonicalMachineProfile(
                normalized.Components.Select(item =>
                    item.SemanticSignature));
            if (!governedProfileSet.Contains(normalizedProfile) ||
                normalized.Components.Any(component =>
                    !definitions.TryGetValue(
                        component.SemanticSignature,
                        out var definition) ||
                    definition.Length != 1 ||
                    !string.Equals(
                        definition[0],
                        CanonicalMachineDefinitionIdentity(
                            component.Dimension,
                            component.Value),
                        StringComparison.Ordinal)))
            {
                return CanonicalMachineProposalValidation.Rejected(
                    "canonical_semantic_definition_unsupported");
            }

            var established = await _curriculum!
                .AnalyzeShadowSourceSemanticsAsync(
                    proposal.SourceLanguageCode,
                    example.SourceText,
                    cancellationToken);
            if (string.Equals(
                    established.State,
                    LegendShadowSourceUnderstanding.Ambiguous,
                    StringComparison.Ordinal))
            {
                return CanonicalMachineProposalValidation.Rejected(
                    "canonical_source_semantics_ambiguous");
            }
            if (string.Equals(
                    established.State,
                    LegendShadowSourceUnderstanding
                        .SupportedForShadowEvaluation,
                    StringComparison.Ordinal))
            {
                var proposedComponents = normalized.Components
                    .Select(item => CanonicalMachineComponentIdentity(
                        item.Dimension,
                        item.Value,
                        item.SurfaceForm))
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                var establishedComponents = established.Components
                    .Select(item => CanonicalMachineComponentIdentity(
                        item.Dimension,
                        item.Value,
                        item.SurfaceForm))
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                if (!proposedComponents.SequenceEqual(
                        establishedComponents,
                        StringComparer.Ordinal))
                {
                    return CanonicalMachineProposalValidation.Rejected(
                        "canonical_source_semantics_contradicted");
                }
            }
            else
            {
                // The surface sentence itself is intentionally held out. Its
                // semantic profile is authorized by independent Founder
                // definitions and contrasts above, not by exact preexistence.
                containsNovelBehavior = true;
            }

            if (!string.IsNullOrWhiteSpace(example.TargetText))
            {
                var normalizedTarget =
                    LegendLanguageIdentity.NormalizeText(
                        example.TargetText);
                if (string.IsNullOrWhiteSpace(normalizedTarget) ||
                    normalizedTarget.Length > 10_000)
                {
                    return CanonicalMachineProposalValidation.Rejected(
                        "canonical_target_constraints_invalid");
                }

                var formulation = await _curriculum
                    .FormulateShadowTargetAsync(
                        proposal.SourceLanguageCode,
                        proposal.TargetLanguageCode,
                        example.SourceText,
                        cancellationToken);
                if (string.Equals(
                        formulation.State,
                        LegendShadowTargetFormulation.Ambiguous,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        formulation.State,
                        LegendShadowTargetFormulation.Contradicted,
                        StringComparison.Ordinal))
                {
                    return CanonicalMachineProposalValidation.Rejected(
                        "canonical_target_formulation_contradicted");
                }
                if (string.Equals(
                        formulation.State,
                        LegendShadowTargetFormulation
                            .SupportedForShadowEvaluation,
                        StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(formulation.Text) ||
                        !string.Equals(
                            LegendLanguageIdentity.NormalizeText(
                                formulation.Text),
                            normalizedTarget,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return CanonicalMachineProposalValidation.Rejected(
                            "canonical_target_text_contradicted");
                    }
                }
                else
                {
                    containsNovelBehavior = true;
                }
            }
        }

        return containsNovelBehavior
            ? CanonicalMachineProposalValidation.Accepted()
            : CanonicalMachineProposalValidation.Rejected(
                "canonical_proposal_already_known");
    }

    private static string CanonicalMachineDefinitionIdentity(
        string dimension,
        string value) =>
        string.Join(
            "|",
            dimension.Trim().ToLowerInvariant(),
            value.Trim().ToLowerInvariant());

    private static string CanonicalMachineProfile(
        IEnumerable<string> semanticSignatures) =>
        string.Join(
            "|",
            semanticSignatures
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal));

    private static string CanonicalMachineContrastIdentity(
        string firstProfile,
        string secondProfile) =>
        string.CompareOrdinal(firstProfile, secondProfile) <= 0
            ? firstProfile + "\n↔\n" + secondProfile
            : secondProfile + "\n↔\n" + firstProfile;

    private sealed record CanonicalMachineProposalValidation(
        bool Succeeded,
        string State,
        string? FailureCode)
    {
        internal static CanonicalMachineProposalValidation Accepted() =>
            new(true, "SystemValidated", null);

        internal static CanonicalMachineProposalValidation Rejected(
            string failureCode) =>
            new(false, "Rejected", failureCode);

        internal static CanonicalMachineProposalValidation Insufficient(
            string failureCode) =>
            new(false, "InsufficientEvidence", failureCode);
    }

    private async Task<LegendLanguageTeacherProposal?>
        TryClaimCanonicalLanguageProposalAsync(
            int maximumAttempts,
            CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(10);

        var proposalId =
            await _db.Set<LegendLanguageTeacherProposal>()
                .AsNoTracking()
                .Where(item =>
                    item.CriticApproved &&
                    item.Provenance == "MachineProposed" &&
                    item.CanonicalValidationAttemptCount <
                        maximumAttempts &&
                    (
                        item.ValidationState ==
                            "AwaitingCanonicalValidation" ||
                        (
                            item.ValidationState ==
                                "CanonicalValidationProcessing" &&
                            item.CanonicalValidationLeaseExpiresUtc !=
                                null &&
                            item.CanonicalValidationLeaseExpiresUtc <
                                now
                        )
                    ))
                .OrderBy(item => item.CreatedUtc)
                .Select(item => (Guid?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (proposalId is null)
            return null;

        if (!_db.Database.IsRelational())
        {
            var proposal =
                await _db.Set<LegendLanguageTeacherProposal>()
                    .SingleOrDefaultAsync(
                        item =>
                            item.Id == proposalId.Value &&
                            item.CriticApproved &&
                            item.Provenance ==
                                "MachineProposed" &&
                            item.CanonicalValidationAttemptCount <
                                maximumAttempts &&
                            (
                                item.ValidationState ==
                                    "AwaitingCanonicalValidation" ||
                                (
                                    item.ValidationState ==
                                        "CanonicalValidationProcessing" &&
                                    item.CanonicalValidationLeaseExpiresUtc !=
                                        null &&
                                    item.CanonicalValidationLeaseExpiresUtc <
                                        now
                                )
                            ),
                        cancellationToken);

            if (proposal is null)
                return null;

            proposal.ValidationState =
                "CanonicalValidationProcessing";
            proposal.CanonicalValidationAttemptCount++;
            proposal.CanonicalValidationLeaseExpiresUtc = expires;
            proposal.UpdatedUtc = now;

            await _db.SaveChangesAsync(cancellationToken);
            return proposal;
        }

        var claimed =
            await _db.Set<LegendLanguageTeacherProposal>()
                .Where(item =>
                    item.Id == proposalId.Value &&
                    item.CriticApproved &&
                    item.Provenance == "MachineProposed" &&
                    item.CanonicalValidationAttemptCount <
                        maximumAttempts &&
                    (
                        item.ValidationState ==
                            "AwaitingCanonicalValidation" ||
                        (
                            item.ValidationState ==
                                "CanonicalValidationProcessing" &&
                            item.CanonicalValidationLeaseExpiresUtc !=
                                null &&
                            item.CanonicalValidationLeaseExpiresUtc <
                                now
                        )
                    ))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            item => item.ValidationState,
                            "CanonicalValidationProcessing")
                        .SetProperty(
                            item =>
                                item.CanonicalValidationAttemptCount,
                            item =>
                                item.CanonicalValidationAttemptCount +
                                1)
                        .SetProperty(
                            item =>
                                item.CanonicalValidationLeaseExpiresUtc,
                            expires)
                        .SetProperty(
                            item => item.UpdatedUtc,
                            now),
                    cancellationToken);

        return claimed == 1
            ? await _db.Set<LegendLanguageTeacherProposal>()
                .SingleAsync(
                    item => item.Id == proposalId.Value,
                    cancellationToken)
            : null;
    }

    private async Task CompleteCanonicalLanguageProposalAsync(
        LegendLanguageTeacherProposal proposal,
        string state,
        string failureCode,
        CancellationToken cancellationToken)
    {
        proposal.ValidationState = state;
        proposal.CanonicalValidationFailureCode =
            failureCode[..Math.Min(failureCode.Length, 160)];
        proposal.CanonicalValidationLeaseExpiresUtc = null;
        proposal.CanonicalValidatedUtc = DateTime.UtcNow;
        proposal.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        await RecordAsync(
            "CanonicalLanguageProposal",
            state == "Rejected" ? "Warning" : "Info",
            state,
            proposal.SourceLanguageCode,
            proposal.PairKey,
            proposal.CanonicalValidationFailureCode,
            "Canonical machine-proposal validation completed without writing corpus or curriculum knowledge.",
            cancellationToken);
    }

    private static string CanonicalMachineComponentIdentity(
        string dimension,
        string value,
        string surfaceForm) =>
        string.Join(
            "|",
            dimension.Trim().ToLowerInvariant(),
            value.Trim().ToLowerInvariant(),
            LegendLanguageIdentity.NormalizeText(surfaceForm)
                .ToLowerInvariant());

    private async Task<bool> TryProcessPendingLanguageProposalAsync(
        CancellationToken cancellationToken)
    {
        var maximumAttempts = Math.Clamp(
            _configuration.GetValue<int?>(
                "LegendConnect:LanguageTeacher:MaximumAutonomousAttempts") ?? 3,
            1,
            5);

        var selected = await SelectLanguageProposalCandidateAsync(
            maximumAttempts,
            cancellationToken);
        if (selected is null)
            return false;

        var requiredRoles = selected.CriticOnly
            ? new[] { LegendLanguageTeacherRole.Critic }
            : new[]
            {
                LegendLanguageTeacherRole.Teacher,
                LegendLanguageTeacherRole.Critic
            };
        var preflights = new Dictionary<
            string,
            LegendLanguageTeacherConfigurationPreflight>(
                StringComparer.Ordinal);
        var preflightSucceeded = true;
        foreach (var role in requiredRoles)
        {
            var preflight = _languageTeacher!.Preflight(role);
            preflights[role] = preflight;
            if (!await PreflightLanguageTeacherRoleAsync(
                    selected,
                    preflight,
                    cancellationToken))
            {
                preflightSucceeded = false;
            }
        }

        if (!preflightSucceeded)
        {
            // The candidate remains unleased. Configuration absence or an
            // open provider circuit never consumes an attempt. All required
            // roles are inspected so each role/fingerprint issue is visible.
            return true;
        }

        var candidate = await TryClaimLanguageProposalCandidateAsync(
            selected.Id,
            maximumAttempts,
            cancellationToken);
        if (candidate is null)
            return false;

        var pairKey = LegendLanguageIdentity.PairKey(
            candidate.SourceLanguageCode,
            candidate.TargetLanguageCode);

        var requestBuild = await BuildLanguageProposalRequestAsync(
            candidate,
            cancellationToken);
        var request = requestBuild.Request;

        if (request is null)
        {
            candidate.TeacherProposalProcessingState = "InsufficientEvidence";
            candidate.TeacherProposalFailureCode =
                requestBuild.FailureCode ??
                "language_teacher_insufficient_governed_evidence";
            candidate.TeacherProposalLeaseExpiresUtc = null;
            candidate.TeacherProposalProcessedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);

            await RecordAsync(
                "LanguageTeacherProposal",
                "Info",
                "InsufficientEvidence",
                candidate.SourceLanguageCode,
                pairKey,
                candidate.TeacherProposalFailureCode,
                "Autonomous proposal generation was skipped because the candidate lacked sufficient governed evidence.",
                cancellationToken);

            return true;
        }

        if (IsConversationMachineCandidate(candidate))
        {
            var researchObservation = string.Equals(
                candidate.Provenance,
                ExternalObservationProvenance,
                StringComparison.Ordinal);
            await RecordAsync(
                researchObservation
                    ? "ResearchExternalObservationProposal"
                    : "ConversationMachineProposal",
                "Info",
                "CriticRequested",
                candidate.SourceLanguageCode,
                pairKey,
                null,
                researchObservation
                    ? "The existing autonomous authority sent the retained ExternalObservation MachineProposed artifact to the existing independent critic."
                    : "The existing autonomous authority sent the retained conversational MachineProposed artifact to the existing independent critic.",
                cancellationToken);

            return await ProcessConversationMachineCritiqueAsync(
                candidate,
                request,
                preflights[LegendLanguageTeacherRole.Critic],
                maximumAttempts,
                cancellationToken);
        }

        await RecordAsync(
            "LanguageTeacherProposal",
            "Info",
            "Requested",
            candidate.SourceLanguageCode,
            pairKey,
            null,
            "The existing autonomous authority requested a bounded machine proposal from governed evidence.",
            cancellationToken);

        LegendLanguageTeacherProposalResult teacherResult;
        try
        {
            teacherResult = await _languageTeacher!.ProposeAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await HandleLanguageTeacherFailureAsync(
                candidate,
                preflights[LegendLanguageTeacherRole.Teacher],
                ClassifyLanguageTeacherException(exception),
                maximumAttempts,
                CancellationToken.None);

            return true;
        }

        if (!teacherResult.Succeeded || teacherResult.Families.Count == 0)
        {
            await HandleLanguageTeacherFailureAsync(
                candidate,
                preflights[LegendLanguageTeacherRole.Teacher],
                teacherResult.Succeeded
                    ? LegendLanguageTeacherFailureClassification.Parsing
                    : NormalizeLanguageTeacherFailureCode(
                        teacherResult.ErrorCode,
                        LegendLanguageTeacherFailureClassification.Provider),
                maximumAttempts,
                cancellationToken);

            return true;
        }

        await ResolveLanguageTeacherCircuitAsync(
            preflights[LegendLanguageTeacherRole.Teacher],
            "ProviderRecovered",
            cancellationToken);

        // Phase-2 already bounds this to four; autonomous Phase-3 orchestration
        // deliberately narrows it further to two families per candidate.
        var families = teacherResult.Families.Take(2).ToArray();
        var critiques = new List<(
            LegendLanguageTeacherFamilyProposal Family,
            LegendLanguageTeacherCritiqueResult Critique,
            LegendLanguageTeacherProposalRequest? Request,
            string? PacketFailureCode)>(families.Length);
        var criticProviderCompleted = false;

        foreach (var family in families)
        {
            var criticRequestBuild =
                await BuildLanguageProposalRequestAsync(
                    candidate,
                    cancellationToken,
                    family);
            var criticRequest = criticRequestBuild.Request;
            if (criticRequest is null)
            {
                var packetFailureCode =
                    criticRequestBuild.FailureCode ??
                    "language_critic_relevant_evidence_insufficient";
                critiques.Add((
                    family,
                    new LegendLanguageTeacherCritiqueResult(
                        true,
                        false,
                        null,
                        [
                            packetFailureCode,
                            "critic_packet_rejected"
                        ]),
                    null,
                    packetFailureCode));
                continue;
            }

            LegendLanguageTeacherCritiqueResult critique;

            try
            {
                critique = await _languageTeacher!.CritiqueAsync(
                    new LegendLanguageTeacherCritiqueRequest(
                        criticRequest,
                        family),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await HandleLanguageTeacherFailureAsync(
                    candidate,
                    preflights[LegendLanguageTeacherRole.Critic],
                    ClassifyLanguageTeacherException(exception),
                    maximumAttempts,
                    CancellationToken.None);

                return true;
            }

            if (!critique.Succeeded)
            {
                await HandleLanguageTeacherFailureAsync(
                    candidate,
                    preflights[LegendLanguageTeacherRole.Critic],
                    NormalizeLanguageTeacherFailureCode(
                        critique.ErrorCode,
                        LegendLanguageTeacherFailureClassification.Provider),
                    maximumAttempts,
                    cancellationToken);

                return true;
            }

            criticProviderCompleted = true;
            critiques.Add((family, critique, criticRequest, null));
        }

        if (criticProviderCompleted)
        {
            await ResolveLanguageTeacherCircuitAsync(
                preflights[LegendLanguageTeacherRole.Critic],
                "ProviderRecovered",
                cancellationToken);
        }

        var anyApproved = false;

        foreach (var (family, critique, criticRequest, packetFailureCode) in critiques)
        {
            var payload = JsonSerializer.Serialize(family);
            var evidenceIdentityHash = criticRequest is null
                ? LegendLanguageIdentity.TextHash(
                    string.Join(
                        "|",
                        "critic-packet-rejected:v1",
                        candidate.IdempotencyKey,
                        packetFailureCode ?? string.Empty,
                        payload))
                : LegendLanguageIdentity.TextHash(
                    string.Join(
                        "\n",
                        criticRequest.Evidence
                            .Select(item => item.EvidenceIdentity)
                            .OrderBy(item => item, StringComparer.Ordinal)));
            var proposalIdentity = LegendLanguageIdentity.TextHash(
                string.Join(
                    "|",
                    "language-teacher-proposal:v1",
                    candidate.IdempotencyKey,
                    evidenceIdentityHash,
                    payload));

            var alreadyExists = await _db
                .Set<LegendLanguageTeacherProposal>()
                .AnyAsync(
                    item => item.ProposalIdentity == proposalIdentity,
                    cancellationToken);

            if (alreadyExists)
            {
                anyApproved |= critique.Approved;
                continue;
            }

            var validationState = critique.Approved
                ? "AwaitingCanonicalValidation"
                : "CriticRejected";

            anyApproved |= critique.Approved;

            _db.Set<LegendLanguageTeacherProposal>().Add(
                new LegendLanguageTeacherProposal
                {
                    Id = Guid.NewGuid(),
                    CorpusCandidateId = candidate.Id,
                    ProposalIdentity = proposalIdentity,
                    PairKey = pairKey,
                    SourceLanguageCode = candidate.SourceLanguageCode,
                    TargetLanguageCode = candidate.TargetLanguageCode,
                    EvidenceIdentityHash = evidenceIdentityHash,
                    FamilyKey = family.FamilyKey,
                    SemanticCategory = family.SemanticCategory,
                    Rationale = family.Rationale,
                    Confidence = Math.Clamp(
                        family.Confidence,
                        0m,
                        1m),
                    ProposalPayloadJson = payload,
                    CriticApproved = critique.Approved,
                    CriticConfidence = critique.Confidence is null
                        ? null
                        : Math.Clamp(
                            critique.Confidence.Value,
                            0m,
                            1m),
                    CriticReasonCodesJson = JsonSerializer.Serialize(
                        critique.ReasonCodes),
                    ValidationState = validationState,
                    Provenance = "MachineProposed",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                });
        }

        candidate.TeacherProposalProcessingState = anyApproved
            ? "AwaitingCanonicalValidation"
            : "CriticRejected";
        candidate.TeacherProposalFailureCode = anyApproved
            ? null
            : critiques
                .Select(item => item.PacketFailureCode)
                .FirstOrDefault(item => item is not null);
        candidate.TeacherProposalLeaseExpiresUtc = null;
        candidate.TeacherProposalProcessedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        await RecordAsync(
            "LanguageTeacherProposal",
            "Info",
            candidate.TeacherProposalProcessingState,
            candidate.SourceLanguageCode,
            pairKey,
            null,
            anyApproved
                ? "Machine proposal artifacts survived independent critique and now await canonical LEGEND validation."
                : "Machine proposal artifacts were rejected by the independent critic and cannot enter canonical knowledge.",
            cancellationToken);

        return true;
    }

    private async Task<LegendLanguageTeacherProposalRequest?>
        BuildGovernedSameLanguageMachineProposalRequestAsync(
            string sourceLanguageCode,
            string targetLanguageCode,
            LegendLanguageTeacherFamilyProposal family,
            CancellationToken cancellationToken)
    {
        if (_curriculum is null ||
            !string.Equals(
                sourceLanguageCode,
                targetLanguageCode,
                StringComparison.OrdinalIgnoreCase) ||
            !LegendConnectMachineTeachingSubmission.IsSupportedIdentity(
                family.CapabilityIdentity,
                family.CategoryIdentity,
                sameLanguage: true) ||
            family.Examples is null ||
            family.Examples.Count is < 2 or > 8)
        {
            return null;
        }

        var semanticEvidence =
            new List<LegendLanguageTeacherEvidence>(
                family.Examples.Count);
        foreach (var example in family.Examples)
        {
            if (!string.IsNullOrWhiteSpace(example.TargetText) ||
                example.Components is null ||
                example.Components.Count is < 1 or > 16)
            {
                return null;
            }

            var understanding =
                await _curriculum.AnalyzeShadowSourceSemanticsAsync(
                    sourceLanguageCode,
                    example.SourceText,
                    cancellationToken);
            if (!string.Equals(
                    understanding.State,
                    LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var proposedComponents = example.Components
                .Select(item => CanonicalMachineComponentIdentity(
                    item.Dimension,
                    item.Value,
                    item.SurfaceForm))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var governedComponents = understanding.Components
                .Select(item => CanonicalMachineComponentIdentity(
                    item.Dimension,
                    item.Value,
                    item.SurfaceForm))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (!proposedComponents.SequenceEqual(
                    governedComponents,
                    StringComparer.Ordinal))
            {
                return null;
            }

            var evidenceIdentity =
                "semantic:" + LegendLanguageIdentity.TextHash(
                    string.Join(
                        "|",
                        sourceLanguageCode,
                        LegendLanguageIdentity.NormalizeText(example.SourceText),
                        string.Join(",", understanding.Components
                            .Select(item => item.SemanticSignature)
                            .OrderBy(item => item, StringComparer.Ordinal))));
            semanticEvidence.Add(
                new LegendLanguageTeacherEvidence(
                    evidenceIdentity,
                    LegendLanguageIdentity.NormalizeText(example.SourceText),
                    null,
                    LegendConnectKnowledgeProvenance.FounderApproved,
                    "GovernedSemanticPrimitive"));
        }

        return semanticEvidence.Count == family.Examples.Count
            ? new LegendLanguageTeacherProposalRequest(
                sourceLanguageCode,
                targetLanguageCode,
                family.SemanticCategory,
                semanticEvidence,
                MaximumFamilies: 1,
                CapabilityIdentity: family.CapabilityIdentity,
                CategoryIdentity: family.CategoryIdentity,
                SemanticFamilyKey: family.FamilyKey,
                SemanticCategory: family.SemanticCategory)
            : null;
    }

    private async Task<LanguageProposalRequestBuildResult>
        BuildLanguageProposalRequestAsync(
            LegendCorpusCandidate candidate,
            CancellationToken cancellationToken,
            LegendLanguageTeacherFamilyProposal? submittedConversationFamily = null)
    {
        var pairKey = LegendLanguageIdentity.PairKey(
            candidate.SourceLanguageCode,
            candidate.TargetLanguageCode);

        var isConversationMachineCandidate =
            IsConversationMachineCandidate(
                candidate);

        LegendLanguageTeacherFamilyProposal? conversationFamily =
            submittedConversationFamily;
        if (isConversationMachineCandidate && conversationFamily is null)
        {
            var payload = await _db.Set<LegendLanguageTeacherProposal>()
                .AsNoTracking()
                .Where(item =>
                    item.CorpusCandidateId == candidate.Id &&
                    item.Provenance == "MachineProposed")
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => item.ProposalPayloadJson)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_proposal_lineage_unavailable");
            }
            try
            {
                conversationFamily =
                    JsonSerializer.Deserialize<LegendLanguageTeacherFamilyProposal>(
                        payload);
            }
            catch (JsonException)
            {
                return LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_proposal_lineage_invalid");
            }
        }

        var sameLanguage = string.Equals(
            candidate.SourceLanguageCode,
            candidate.TargetLanguageCode,
            StringComparison.OrdinalIgnoreCase);
        if (isConversationMachineCandidate &&
            (conversationFamily is null ||
             conversationFamily.Examples is null ||
             conversationFamily.Examples.Count is < 2 or > 8 ||
             !LegendConnectMachineTeachingSubmission.IsSupportedIdentity(
                 conversationFamily.CapabilityIdentity,
                 conversationFamily.CategoryIdentity,
                 sameLanguage) ||
             !string.Equals(
                 candidate.Category,
                 LegendConnectMachineTeachingSubmission.CandidateCategoryIdentity(
                     conversationFamily.CapabilityIdentity,
                     conversationFamily.CategoryIdentity),
                 StringComparison.Ordinal)))
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_proposal_lineage_invalid");
        }

        var proposedLineage = conversationFamily is null
            ? null
            : LegendConnectCurriculumService
                .NormalizeMachineTeachingSemanticLineage(
                    conversationFamily);
        if (conversationFamily is not null && proposedLineage is null)
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_semantic_lineage_invalid");
        }

        if (string.Equals(
                candidate.Provenance,
                ExternalObservationProvenance,
                StringComparison.Ordinal))
        {
            var researchRequest = conversationFamily is null
                ? null
                : BuildResearchObservationProposalRequest(
                    candidate.SourceLanguageCode,
                    candidate.TargetLanguageCode,
                    conversationFamily);
            return researchRequest is null
                ? LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_research_observation_lineage_invalid")
                : LanguageProposalRequestBuildResult.Accepted(researchRequest);
        }

        LegendLanguageTextUnit? source = null;

        if (!isConversationMachineCandidate)
        {
            source =
                await _db.Set<LegendLanguageTextUnit>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        item =>
                            item.LanguageCode ==
                                candidate.SourceLanguageCode &&
                            item.NormalizedHash ==
                                candidate.SourceTextHash &&
                            item.IsTrainingEligible,
                        cancellationToken);

            if (source is null ||
                !string.Equals(
                    source.Text,
                    LegendLanguageIdentity.NormalizeText(
                        candidate.SourceText),
                    StringComparison.Ordinal))
            {
                return LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_source_lineage_unavailable");
            }
        }
        else
        {
            var normalizedSource =
                LegendLanguageIdentity.NormalizeText(
                    candidate.SourceText);

            if (string.IsNullOrWhiteSpace(
                    normalizedSource) ||
                !string.Equals(
                    candidate.SourceTextHash,
                    LegendLanguageIdentity.TextHash(
                        normalizedSource),
                    StringComparison.Ordinal))
            {
                return LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_source_lineage_invalid");
            }
        }

        if (isConversationMachineCandidate && sameLanguage)
        {
            var sameLanguageRequest =
                await BuildGovernedSameLanguageMachineProposalRequestAsync(
                    candidate.SourceLanguageCode,
                    candidate.TargetLanguageCode,
                    conversationFamily!,
                    cancellationToken);
            return sameLanguageRequest is null
                ? LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_same_language_lineage_unproven")
                : LanguageProposalRequestBuildResult.Accepted(
                    sameLanguageRequest);
        }

        LegendCurriculumFamily? governedFamily;
        if (candidate.CurriculumFamilyId is Guid governedFamilyId)
        {
            governedFamily = await _db.Set<LegendCurriculumFamily>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == governedFamilyId,
                    cancellationToken);
        }
        else if (proposedLineage is not null)
        {
            var matchingFamilies = await _db.Set<LegendCurriculumFamily>()
                .AsNoTracking()
                .Where(item => item.FamilyKey == proposedLineage.FamilyKey)
                .Take(2)
                .ToListAsync(cancellationToken);
            var matchedFamily = matchingFamilies.Count == 1
                ? matchingFamilies[0]
                : null;
            var matchedCategory = matchedFamily is null ||
                string.IsNullOrWhiteSpace(matchedFamily.SemanticCategory)
                    ? matchedFamily?.FamilyKey
                    : matchedFamily.SemanticCategory.Trim();
            governedFamily = matchedFamily is not null &&
                string.Equals(
                    matchedCategory,
                    proposedLineage.SemanticCategory,
                    StringComparison.OrdinalIgnoreCase)
                        ? matchedFamily
                        : null;
        }
        else
        {
            governedFamily = null;
        }

        if (governedFamily is null)
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_semantic_family_lineage_unproven");
        }

        var governedSemanticCategory =
            string.IsNullOrWhiteSpace(governedFamily.SemanticCategory)
                ? governedFamily.FamilyKey
                : governedFamily.SemanticCategory.Trim();

        if (proposedLineage is not null &&
            (!string.Equals(
                 governedFamily.FamilyKey,
                 proposedLineage.FamilyKey,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 governedSemanticCategory,
                 proposedLineage.SemanticCategory,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_semantic_family_lineage_mismatch");
        }

        var familyExamples = await (
            from example in _db.Set<LegendCurriculumExample>()
                .AsNoTracking()
            join unit in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on example.TextUnitId equals unit.Id
            where
                example.CurriculumFamilyId == governedFamily.Id &&
                example.SupersededUtc == null &&
                unit.IsTrainingEligible &&
                example.LanguageCode == unit.LanguageCode &&
                (example.LanguageCode == candidate.SourceLanguageCode ||
                 example.LanguageCode == candidate.TargetLanguageCode)
            select new LanguageFamilyExampleLineage(
                example.Id,
                example.TextUnitId,
                example.LanguageCode,
                example.DerivedFromCurriculumExampleId))
            .ToListAsync(cancellationToken);

        var sourceExamples = familyExamples
            .Where(item => string.Equals(
                item.LanguageCode,
                candidate.SourceLanguageCode,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var targetExamples = familyExamples
            .Where(item => string.Equals(
                item.LanguageCode,
                candidate.TargetLanguageCode,
                StringComparison.OrdinalIgnoreCase) &&
                item.DerivedFromCurriculumExampleId is not null)
            .ToArray();
        if (sourceExamples.Length < 2 || targetExamples.Length < 2)
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_family_examples_insufficient");
        }

        LanguageFamilyExampleLineage? candidateSourceExample = null;
        if (!isConversationMachineCandidate)
        {
            candidateSourceExample = candidate.SourceCurriculumExampleId is Guid sourceExampleId
                ? sourceExamples.SingleOrDefault(item =>
                    item.Id == sourceExampleId &&
                    item.TextUnitId == source!.Id)
                : null;
            if (candidateSourceExample is null)
            {
                return LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_candidate_family_lineage_unproven");
            }
        }

        var allExampleIds = sourceExamples
            .Select(item => item.Id)
            .Concat(targetExamples.Select(item => item.Id))
            .Distinct()
            .ToArray();
        var primitiveRows = await _db
            .Set<LegendLanguageCompositionalAnchor>()
            .AsNoTracking()
            .Where(item =>
                allExampleIds.Contains(item.CurriculumExampleId) &&
                item.CurriculumFamilyId == governedFamily.Id &&
                item.SupersededUtc == null &&
                item.SemanticSignature != null &&
                item.SemanticSignature != string.Empty &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new
            {
                item.CurriculumExampleId,
                SemanticSignature = item.SemanticSignature!
            })
            .Distinct()
            .ToListAsync(cancellationToken);
        var primitivesByExample = primitiveRows
            .GroupBy(item => item.CurriculumExampleId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(item => item.SemanticSignature)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray());

        var sourceExampleIds = sourceExamples
            .Select(item => item.Id)
            .ToArray();
        var transitions = await _db
            .Set<LegendSemanticTransitionEvidence>()
            .AsNoTracking()
            .Where(item =>
                sourceExampleIds.Contains(item.SourceCurriculumExampleId) &&
                sourceExampleIds.Contains(item.ResultCurriculumExampleId) &&
                item.SourceLanguageCode == candidate.SourceLanguageCode &&
                item.ResultLanguageCode == candidate.SourceLanguageCode &&
                item.SupersededUtc == null &&
                item.ContributionState == "Supported" &&
                item.IsHumanVerifiedSupport &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new LanguageTransitionLineage(
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId,
                item.IndependentSourceIdentity))
            .ToListAsync(cancellationToken);

        var transitionSignatures = transitions
            .Select(item => item.TransitionSignature)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (proposedLineage is { TransitionSignatures.Count: > 0 } &&
            proposedLineage.TransitionSignatures.Any(
                item => !transitionSignatures.Contains(item)))
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_semantic_transition_lineage_unproven");
        }

        var selectedTransitionSignatures = proposedLineage is
            { TransitionSignatures.Count: > 0 }
                ? proposedLineage.TransitionSignatures.ToHashSet(
                    StringComparer.Ordinal)
                : transitionSignatures;
        var selectedTransitions = transitions
            .Where(item => selectedTransitionSignatures.Contains(
                item.TransitionSignature))
            .ToArray();
        var transitionExampleIds = selectedTransitions
            .Select(item => item.SourceCurriculumExampleId)
            .Concat(selectedTransitions.Select(
                item => item.ResultCurriculumExampleId))
            .ToHashSet();

        var contrasts = await _db
            .Set<LegendLanguageStructuralEvidence>()
            .AsNoTracking()
            .Where(item =>
                item.CurriculumFamilyId == governedFamily.Id &&
                item.LanguageCode == candidate.SourceLanguageCode &&
                item.PairKey == string.Empty &&
                sourceExampleIds.Contains(item.BaselineCurriculumExampleId) &&
                sourceExampleIds.Contains(item.ComparedCurriculumExampleId) &&
                item.SupersededUtc == null &&
                item.ContributionState == "Supported" &&
                item.IsHumanVerifiedSupport &&
                item.Provenance ==
                    LegendConnectKnowledgeProvenance.FounderApproved)
            .Select(item => new LanguageControlledContrastLineage(
                item.EvidenceSignature,
                item.BaselineCurriculumExampleId,
                item.ComparedCurriculumExampleId,
                item.IndependentSourceIdentity))
            .ToListAsync(cancellationToken);
        var contrastExampleIds = contrasts
            .Select(item => item.BaselineCurriculumExampleId)
            .Concat(contrasts.Select(item =>
                item.ComparedCurriculumExampleId))
            .ToHashSet();
        if (contrasts.Count == 0 || contrastExampleIds.Count < 2)
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_controlled_contrast_lineage_unproven");
        }

        if (proposedLineage is not null)
        {
            var governedPrimitiveProfiles = sourceExamples
                .Where(item => primitivesByExample.ContainsKey(item.Id))
                .Select(item => string.Join(
                    "|",
                    primitivesByExample[item.Id]))
                .ToHashSet(StringComparer.Ordinal);
            var proposedPrimitiveProfiles = proposedLineage.Examples
                .Select(item => string.Join(
                    "|",
                    item.PrimitiveSignatures))
                .ToArray();
            if (proposedPrimitiveProfiles.Distinct(
                    StringComparer.Ordinal).Count() < 2 ||
                proposedPrimitiveProfiles.Any(
                    item => !governedPrimitiveProfiles.Contains(item)))
            {
                return LanguageProposalRequestBuildResult.Rejected(
                    "language_teacher_semantic_primitive_lineage_unproven");
            }
        }

        var candidateSourcePrimitives = candidateSourceExample is null
            ? []
            : primitivesByExample.GetValueOrDefault(
                candidateSourceExample.Id) ?? [];
        var candidateSourceTransitions = candidateSourceExample is null
            ? []
            : selectedTransitions
                .Where(item =>
                    item.SourceCurriculumExampleId == candidateSourceExample.Id ||
                    item.ResultCurriculumExampleId == candidateSourceExample.Id)
                .Select(item =>
                    item.TransitionSignature + ":" +
                    item.IndependentSourceIdentity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        var candidateSourceContrasts = candidateSourceExample is null
            ? []
            : contrasts
                .Where(item =>
                    item.BaselineCurriculumExampleId == candidateSourceExample.Id ||
                    item.ComparedCurriculumExampleId == candidateSourceExample.Id)
                .Select(item =>
                    item.EvidenceSignature + ":" +
                    item.IndependentSourceIdentity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        if (candidateSourceExample is not null &&
            (candidateSourcePrimitives.Count == 0 ||
             candidateSourceContrasts.Length == 0 ||
             (selectedTransitions.Length > 0 &&
              candidateSourceTransitions.Length == 0)))
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_candidate_semantic_lineage_unproven");
        }

        var sourceTextUnitIds = sourceExamples
            .Select(item => item.TextUnitId)
            .Distinct()
            .ToArray();
        var targetTextUnitIds = targetExamples
            .Select(item => item.TextUnitId)
            .Distinct()
            .ToArray();
        // This is the single trusted-alignment query used by proposal and
        // critic packets. It is bounded first by durable family/example
        // lineage; there is no language-pair-wide candidate pool to filter
        // after selection.
        var trusted = await (
            from alignment in _db.Set<LegendTranslationAlignment>()
                .AsNoTracking()
            join evidenceSource in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on alignment.SourceTextUnitId equals evidenceSource.Id
            join evidenceTarget in _db.Set<LegendLanguageTextUnit>()
                .AsNoTracking()
                on alignment.TargetTextUnitId equals evidenceTarget.Id
            where
                alignment.PairKey == pairKey &&
                alignment.SupersededUtc == null &&
                sourceTextUnitIds.Contains(alignment.SourceTextUnitId) &&
                targetTextUnitIds.Contains(alignment.TargetTextUnitId) &&
                evidenceSource.IsTrainingEligible &&
                evidenceTarget.IsTrainingEligible &&
                evidenceSource.LanguageCode == candidate.SourceLanguageCode &&
                evidenceTarget.LanguageCode == candidate.TargetLanguageCode &&
                (alignment.HumanVerified ||
                 alignment.QualityState == "SystemValidated")
            select new TrustedLanguageAlignment(
                alignment.Id,
                alignment.SourceTextUnitId,
                alignment.TargetTextUnitId,
                alignment.HumanVerified,
                alignment.Confidence,
                alignment.Provenance,
                alignment.QualityState,
                evidenceSource.Text,
                evidenceTarget.Text))
            .ToListAsync(cancellationToken);

        var machineValidatedIds = trusted
            .Where(item => !item.HumanVerified)
            .Select(item => item.Id)
            .ToArray();

        HashSet<Guid> contradicted = machineValidatedIds.Length == 0
            ? []
            : (await _db.Set<LegendTranslationQualityEvidence>()
                .AsNoTracking()
                .Where(item =>
                    machineValidatedIds.Contains(
                        item.ObservedAlignmentId) &&
                    item.Signal == "Contradictory" &&
                    item.ResolutionState == "Open" &&
                    item.SupersededUtc == null)
                .Select(item => item.ObservedAlignmentId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();

        var lineageRows = new List<TrustedLanguageAlignmentLineage>();
        foreach (var row in trusted.Where(item =>
                     item.HumanVerified ||
                     !contradicted.Contains(item.Id)))
        {
            foreach (var sourceExample in sourceExamples.Where(item =>
                         item.TextUnitId == row.SourceTextUnitId &&
                         contrastExampleIds.Contains(item.Id) &&
                         (transitionExampleIds.Count == 0 ||
                          transitionExampleIds.Contains(item.Id))))
            {
                foreach (var targetExample in targetExamples.Where(item =>
                             item.TextUnitId == row.TargetTextUnitId &&
                             item.DerivedFromCurriculumExampleId ==
                                sourceExample.Id))
                {
                    var sourcePrimitives = primitivesByExample
                        .GetValueOrDefault(sourceExample.Id) ?? [];
                    var targetPrimitives = primitivesByExample
                        .GetValueOrDefault(targetExample.Id) ?? [];
                    if (sourcePrimitives.Count == 0 ||
                        !sourcePrimitives.SequenceEqual(
                            targetPrimitives,
                            StringComparer.Ordinal))
                    {
                        continue;
                    }

                    var rowTransitions = selectedTransitions
                        .Where(item =>
                            item.SourceCurriculumExampleId == sourceExample.Id ||
                            item.ResultCurriculumExampleId == sourceExample.Id)
                        .Select(item =>
                            item.TransitionSignature + ":" +
                            item.IndependentSourceIdentity)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray();
                    var rowContrasts = contrasts
                        .Where(item =>
                            item.BaselineCurriculumExampleId == sourceExample.Id ||
                            item.ComparedCurriculumExampleId == sourceExample.Id)
                        .Select(item =>
                            item.EvidenceSignature + ":" +
                            item.IndependentSourceIdentity)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray();
                    if (rowContrasts.Length == 0 ||
                        (selectedTransitions.Length > 0 &&
                         rowTransitions.Length == 0))
                    {
                        continue;
                    }

                    var standardIdentity = row.HumanVerified
                        ? "HumanVerified"
                        : "SystemValidated";
                    var durableIdentity = LegendLanguageIdentity.TextHash(
                        string.Join(
                            "|",
                            "language-critic-lineage:v1",
                            governedFamily.Id.ToString("N"),
                            sourceExample.Id.ToString("N"),
                            targetExample.Id.ToString("N"),
                            standardIdentity,
                            row.Provenance,
                            row.QualityState,
                            string.Join(",", sourcePrimitives),
                            string.Join(",", rowTransitions),
                            string.Join(",", rowContrasts)));
                    lineageRows.Add(
                        new TrustedLanguageAlignmentLineage(
                            row,
                            sourceExample,
                            targetExample,
                            "lineage:" + durableIdentity));
                }
            }
        }

        // Multiple physical alignments for the same durable example lineage
        // cannot manufacture critic support. Prefer the existing higher
        // evidence standard, then choose a stable row without exposing its
        // physical identity in the packet.
        var selectedLineages = lineageRows
            .GroupBy(
                item => new
                {
                    item.SourceExample.Id,
                    TargetExampleId = item.TargetExample.Id
                })
            .Select(group => group
                .OrderByDescending(item => item.Alignment.HumanVerified)
                .ThenByDescending(item => item.Alignment.Confidence)
                .ThenBy(item => item.Alignment.Provenance, StringComparer.Ordinal)
                .ThenBy(item => item.Alignment.QualityState, StringComparer.Ordinal)
                .ThenBy(item => item.Alignment.Id)
                .First())
            .OrderBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
            .Take(isConversationMachineCandidate ? 32 : 31)
            .ToArray();

        var evidence = new List<LegendLanguageTeacherEvidence>(32);
        if (!isConversationMachineCandidate)
        {
            evidence.Add(
                new LegendLanguageTeacherEvidence(
                    "family-source:" + LegendLanguageIdentity.TextHash(
                        string.Join(
                            "|",
                            "language-critic-source-lineage:v1",
                            governedFamily.Id.ToString("N"),
                            candidateSourceExample!.Id.ToString("N"),
                            source!.Provenance,
                            string.Join(",", candidateSourcePrimitives),
                            string.Join(",", candidateSourceTransitions),
                            string.Join(",", candidateSourceContrasts))),
                    source.Text,
                    null,
                    source.Provenance,
                    "CanonicalSource"));
        }

        foreach (var lineage in selectedLineages)
        {
            evidence.Add(
                new LegendLanguageTeacherEvidence(
                    lineage.EvidenceIdentity,
                    lineage.Alignment.SourceText,
                    lineage.Alignment.TargetText,
                    lineage.Alignment.Provenance,
                    lineage.Alignment.QualityState));
        }

        // A conversational proposal has no canonical source evidence of its
        // own, so it requires two independently lineaged governed contrasts.
        // An autonomous family candidate retains the existing source-plus-one-
        // relationship minimum. Neither path may count duplicate alignments.
        if (evidence.Count < 2 ||
            selectedLineages.Length <
                (isConversationMachineCandidate ? 2 : 1))
        {
            return LanguageProposalRequestBuildResult.Rejected(
                "language_teacher_relevant_evidence_insufficient");
        }

        var learningGoal = string.Equals(
            governedSemanticCategory,
            governedFamily.FamilyKey,
            StringComparison.Ordinal)
                ? governedFamily.FamilyKey
                : $"{governedFamily.FamilyKey} | " +
                  governedSemanticCategory;

        learningGoal = learningGoal.Trim();
        if (learningGoal.Length > 500)
            learningGoal = learningGoal[..500];

        return LanguageProposalRequestBuildResult.Accepted(
            new LegendLanguageTeacherProposalRequest(
                candidate.SourceLanguageCode,
                candidate.TargetLanguageCode,
                learningGoal,
                evidence,
                MaximumFamilies: 2,
                CapabilityIdentity:
                    conversationFamily?.CapabilityIdentity ??
                    LegendConnectMachineTeachingSubmission.TranslationCapability,
                CategoryIdentity:
                    conversationFamily?.CategoryIdentity ??
                    LegendConnectMachineTeachingSubmission.ReusableSemanticCategory,
                SemanticFamilyKey: governedFamily.FamilyKey,
                SemanticCategory: governedSemanticCategory));
    }

    private sealed record LanguageProposalRequestBuildResult(
        LegendLanguageTeacherProposalRequest? Request,
        string? FailureCode)
    {
        internal static LanguageProposalRequestBuildResult Accepted(
            LegendLanguageTeacherProposalRequest request) =>
            new(request, null);

        internal static LanguageProposalRequestBuildResult Rejected(
            string failureCode) =>
            new(null, failureCode);
    }

    private sealed record LanguageFamilyExampleLineage(
        Guid Id,
        Guid TextUnitId,
        string LanguageCode,
        Guid? DerivedFromCurriculumExampleId);

    private sealed record LanguageTransitionLineage(
        string TransitionSignature,
        Guid SourceCurriculumExampleId,
        Guid ResultCurriculumExampleId,
        string IndependentSourceIdentity);

    private sealed record LanguageControlledContrastLineage(
        string EvidenceSignature,
        Guid BaselineCurriculumExampleId,
        Guid ComparedCurriculumExampleId,
        string IndependentSourceIdentity);

    private sealed record TrustedLanguageAlignment(
        Guid Id,
        Guid SourceTextUnitId,
        Guid TargetTextUnitId,
        bool HumanVerified,
        decimal? Confidence,
        string Provenance,
        string QualityState,
        string SourceText,
        string TargetText);

    private sealed record TrustedLanguageAlignmentLineage(
        TrustedLanguageAlignment Alignment,
        LanguageFamilyExampleLineage SourceExample,
        LanguageFamilyExampleLineage TargetExample,
        string EvidenceIdentity);

    private async Task<bool> PreflightLanguageTeacherRoleAsync(
        LanguageProposalWorkCandidate selected,
        LegendLanguageTeacherConfigurationPreflight preflight,
        CancellationToken cancellationToken)
    {
        var normalizedRole = NormalizeLanguageTeacherRole(
            preflight.Role);
        var fingerprint = NormalizeLanguageTeacherFingerprint(
            normalizedRole,
            preflight.ConfigurationFingerprint);
        var normalized = preflight with
        {
            Role = normalizedRole,
            ConfigurationFingerprint = fingerprint,
            FailureCode = preflight.IsReady
                ? null
                : NormalizeLanguageTeacherFailureCode(
                    preflight.FailureCode,
                    LegendLanguageTeacherFailureClassification
                        .ConfigurationInvalid)
        };

        if (normalized.IsReady)
        {
            await ResolveSupersededLanguageTeacherCircuitsAsync(
                normalized,
                cancellationToken);
        }

        if (await IsLanguageTeacherCircuitCoolingAsync(
                normalized,
                cancellationToken))
        {
            return false;
        }

        if (normalized.IsReady)
            return true;

        await RecordLanguageTeacherFailureAsync(
            normalized,
            normalized.FailureCode!,
            selected.SourceLanguageCode,
            LegendLanguageIdentity.PairKey(
                selected.SourceLanguageCode,
                selected.TargetLanguageCode),
            cancellationToken);
        return false;
    }

    private async Task HandleLanguageTeacherFailureAsync(
        LegendCorpusCandidate candidate,
        LegendLanguageTeacherConfigurationPreflight preflight,
        string failureCode,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        var normalizedFailure = NormalizeLanguageTeacherFailureCode(
            failureCode,
            LegendLanguageTeacherFailureClassification.Provider);
        var normalizedPreflight = preflight with
        {
            Role = NormalizeLanguageTeacherRole(preflight.Role),
            ConfigurationFingerprint =
                NormalizeLanguageTeacherFingerprint(
                    preflight.Role,
                    preflight.ConfigurationFingerprint)
        };
        await RecordLanguageTeacherFailureAsync(
            normalizedPreflight,
            normalizedFailure,
            candidate.SourceLanguageCode,
            candidate.PairKey(),
            cancellationToken);

        if (LegendLanguageTeacherFailureClassification
            .IsLocalConfiguration(normalizedFailure))
        {
            // Configuration disappeared after preflight. Return the exact
            // lease attempt because no provider work could have occurred.
            candidate.TeacherProposalAttemptCount = Math.Max(
                0,
                candidate.TeacherProposalAttemptCount - 1);
            candidate.TeacherProposalProcessingState = "Pending";
            candidate.TeacherProposalFailureCode = normalizedFailure;
            candidate.TeacherProposalLeaseExpiresUtc = null;
            candidate.TeacherProposalProcessedUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await DeferLanguageProposalAsync(
            candidate,
            normalizedFailure,
            maximumAttempts,
            DateTime.UtcNow.Add(LanguageTeacherFailureCooldown()),
            cancellationToken);
    }

    private async Task<bool> IsLanguageTeacherCircuitCoolingAsync(
        LegendLanguageTeacherConfigurationPreflight preflight,
        CancellationToken cancellationToken)
    {
        var correlationId = LanguageTeacherCircuitCorrelation(
            preflight.Role,
            preflight.ConfigurationFingerprint);
        var issueId = LanguageTeacherIssueId(correlationId);
        var openIssue = await _db
            .Set<LegendConnectOperationalEvent>()
            .AsNoTracking()
            .AnyAsync(item =>
                item.Id == issueId &&
                item.Category == LanguageTeacherIssueCategory &&
                !item.IsResolved,
                cancellationToken);
        if (!openIssue)
            return false;

        var latestOccurrence = await _db
            .Set<LegendConnectOperationalEvent>()
            .AsNoTracking()
            .Where(item =>
                item.Category == LanguageTeacherFailureCategory &&
                item.CorrelationId == correlationId)
            .Select(item => (DateTime?)item.OccurredUtc)
            .MaxAsync(cancellationToken);
        return latestOccurrence is null ||
            latestOccurrence.Value.Add(
                LanguageTeacherFailureCooldown()) > DateTime.UtcNow;
    }

    private async Task RecordLanguageTeacherFailureAsync(
        LegendLanguageTeacherConfigurationPreflight preflight,
        string failureCode,
        string? languageCode,
        string? pairKey,
        CancellationToken cancellationToken)
    {
        var role = NormalizeLanguageTeacherRole(preflight.Role);
        var fingerprint = NormalizeLanguageTeacherFingerprint(
            role,
            preflight.ConfigurationFingerprint);
        var correlationId = LanguageTeacherCircuitCorrelation(
            role,
            fingerprint);
        var now = DateTime.UtcNow;
        _db.Set<LegendConnectOperationalEvent>().Add(
            new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = LanguageTeacherFailureCategory,
                Severity = "Info",
                Status = "Occurrence",
                LanguageCode = BoundedLanguageTeacherValue(
                    languageCode,
                    32),
                PairKey = BoundedLanguageTeacherValue(pairKey, 72),
                CorrelationId = correlationId,
                ErrorCode = failureCode,
                Summary =
                    $"{role} failure evidence for configuration {fingerprint[..12]}.",
                IsResolved = true,
                OccurredUtc = now
            });
        await _db.SaveChangesAsync(cancellationToken);

        var occurrences = await _db
            .Set<LegendConnectOperationalEvent>()
            .AsNoTracking()
            .Where(item =>
                item.Category == LanguageTeacherFailureCategory &&
                item.CorrelationId == correlationId)
            .Select(item => item.OccurredUtc)
            .ToListAsync(cancellationToken);
        var summary =
            $"{role} configuration {fingerprint[..12]} circuit open; " +
            $"Occurrences={occurrences.Count}; " +
            $"FirstUtc={occurrences.Min():O}; " +
            $"LastUtc={occurrences.Max():O}; " +
            $"Latest={failureCode}.";
        var issueId = LanguageTeacherIssueId(correlationId);
        var issue = await _db.Set<LegendConnectOperationalEvent>()
            .SingleOrDefaultAsync(
                item => item.Id == issueId,
                cancellationToken);
        if (issue is null)
        {
            issue = new LegendConnectOperationalEvent
            {
                Id = issueId,
                Category = LanguageTeacherIssueCategory,
                Severity = LanguageTeacherFailureSeverity(failureCode),
                Status = "CircuitOpen",
                LanguageCode = BoundedLanguageTeacherValue(
                    languageCode,
                    32),
                PairKey = BoundedLanguageTeacherValue(pairKey, 72),
                CorrelationId = correlationId,
                ErrorCode = failureCode,
                Summary = summary[..Math.Min(summary.Length, 500)],
                IsResolved = false,
                OccurredUtc = occurrences.Min()
            };
            _db.Set<LegendConnectOperationalEvent>().Add(issue);
        }
        else
        {
            issue.Severity = LanguageTeacherFailureSeverity(
                failureCode);
            issue.Status = "CircuitOpen";
            issue.LanguageCode ??= BoundedLanguageTeacherValue(
                languageCode,
                32);
            issue.PairKey ??= BoundedLanguageTeacherValue(pairKey, 72);
            issue.ErrorCode = failureCode;
            issue.Summary = summary[..Math.Min(summary.Length, 500)];
            issue.IsResolved = false;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The deterministic issue identity makes concurrent openings one
            // logical issue. Re-read and refresh the winning row so its
            // aggregate remains consistent with every immutable occurrence.
            var entry = _db.Entry(issue);
            if (entry.State != EntityState.Added)
                throw;

            entry.State = EntityState.Detached;
            var persistedIssue = await _db
                .Set<LegendConnectOperationalEvent>()
                .SingleOrDefaultAsync(
                    item => item.Id == issueId,
                    cancellationToken);
            if (persistedIssue is null)
                throw;

            var concurrentOccurrences = await _db
                .Set<LegendConnectOperationalEvent>()
                .AsNoTracking()
                .Where(item =>
                    item.Category == LanguageTeacherFailureCategory &&
                    item.CorrelationId == correlationId)
                .Select(item => item.OccurredUtc)
                .ToListAsync(cancellationToken);
            var concurrentSummary =
                $"{role} configuration {fingerprint[..12]} circuit open; " +
                $"Occurrences={concurrentOccurrences.Count}; " +
                $"FirstUtc={concurrentOccurrences.Min():O}; " +
                $"LastUtc={concurrentOccurrences.Max():O}; " +
                $"Latest={failureCode}.";
            persistedIssue.Severity =
                LanguageTeacherFailureSeverity(failureCode);
            persistedIssue.Status = "CircuitOpen";
            persistedIssue.ErrorCode = failureCode;
            persistedIssue.Summary = concurrentSummary[..Math.Min(
                concurrentSummary.Length,
                500)];
            persistedIssue.IsResolved = false;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ResolveSupersededLanguageTeacherCircuitsAsync(
        LegendLanguageTeacherConfigurationPreflight preflight,
        CancellationToken cancellationToken)
    {
        var rolePrefix = NormalizeLanguageTeacherRole(preflight.Role) + ":";
        var currentCorrelation = LanguageTeacherCircuitCorrelation(
            preflight.Role,
            preflight.ConfigurationFingerprint);
        var superseded = await _db
            .Set<LegendConnectOperationalEvent>()
            .Where(item =>
                item.Category == LanguageTeacherIssueCategory &&
                item.CorrelationId != null &&
                item.CorrelationId.StartsWith(rolePrefix) &&
                item.CorrelationId != currentCorrelation &&
                !item.IsResolved)
            .ToListAsync(cancellationToken);
        foreach (var issue in superseded)
        {
            await ResolveLanguageTeacherIssueAsync(
                issue,
                "ConfigurationChanged",
                cancellationToken);
        }
    }

    private async Task ResolveLanguageTeacherCircuitAsync(
        LegendLanguageTeacherConfigurationPreflight preflight,
        string recoveryStatus,
        CancellationToken cancellationToken)
    {
        var correlationId = LanguageTeacherCircuitCorrelation(
            preflight.Role,
            preflight.ConfigurationFingerprint);
        var issue = await _db.Set<LegendConnectOperationalEvent>()
            .SingleOrDefaultAsync(item =>
                item.Id == LanguageTeacherIssueId(correlationId) &&
                !item.IsResolved,
                cancellationToken);
        if (issue is null)
            return;

        await ResolveLanguageTeacherIssueAsync(
            issue,
            recoveryStatus,
            cancellationToken);
    }

    private async Task ResolveLanguageTeacherIssueAsync(
        LegendConnectOperationalEvent issue,
        string recoveryStatus,
        CancellationToken cancellationToken)
    {
        issue.IsResolved = true;
        issue.Status = recoveryStatus;
        var recoveredUtc = DateTime.UtcNow;
        var existingSummary = issue.Summary ?? string.Empty;
        var summary = existingSummary +
            $" RecoveredUtc={recoveredUtc:O}.";
        issue.Summary = summary[..Math.Min(summary.Length, 500)];
        _db.Set<LegendConnectOperationalEvent>().Add(
            new LegendConnectOperationalEvent
            {
                Id = Guid.NewGuid(),
                Category = LanguageTeacherRecoveryCategory,
                Severity = "Info",
                Status = recoveryStatus,
                LanguageCode = issue.LanguageCode,
                PairKey = issue.PairKey,
                CorrelationId = issue.CorrelationId,
                ErrorCode = issue.ErrorCode,
                Summary =
                    "The existing language teacher/critic circuit recovered; prior failure occurrences remain retained.",
                IsResolved = true,
                OccurredUtc = recoveredUtc
            });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private TimeSpan LanguageTeacherFailureCooldown() =>
        TimeSpan.FromMinutes(
            Math.Clamp(
                _configuration.GetValue<int?>(
                    "LegendConnect:LanguageTeacher:FailureCooldownMinutes") ??
                15,
                1,
                60));

    private static string NormalizeLanguageTeacherRole(string? role) =>
        string.Equals(
            role,
            LegendLanguageTeacherRole.Critic,
            StringComparison.OrdinalIgnoreCase)
                ? LegendLanguageTeacherRole.Critic
                : LegendLanguageTeacherRole.Teacher;

    private static string NormalizeLanguageTeacherFingerprint(
        string role,
        string? fingerprint)
    {
        var normalized = fingerprint?.Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length <= 80
            ? normalized
            : LegendLanguageIdentity.TextHash(
                "invalid-language-teacher-fingerprint|" +
                NormalizeLanguageTeacherRole(role));
    }

    private static string NormalizeLanguageTeacherFailureCode(
        string? failureCode,
        string fallback) =>
        LegendLanguageTeacherFailureClassification.Normalize(
            failureCode,
            fallback);

    private static string ClassifyLanguageTeacherException(
        Exception exception) =>
        LegendLanguageTeacherFailureClassification.FromException(
            exception);

    private static string LanguageTeacherCircuitCorrelation(
        string role,
        string fingerprint) =>
        NormalizeLanguageTeacherRole(role) + ":" +
        NormalizeLanguageTeacherFingerprint(role, fingerprint);

    private static Guid LanguageTeacherIssueId(string correlationId)
    {
        var hash = LegendLanguageIdentity.TextHash(
            "language-teacher-provider-issue:v1|" + correlationId);
        return Guid.ParseExact(hash[..32], "N");
    }

    private static string LanguageTeacherFailureSeverity(
        string failureCode) =>
        failureCode is
            LegendLanguageTeacherFailureClassification.Authentication or
            LegendLanguageTeacherFailureClassification.Schema or
            LegendLanguageTeacherFailureClassification.Parsing
                ? "Error"
                : "Warning";

    private static string? BoundedLanguageTeacherValue(
        string? value,
        int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private sealed record LanguageProposalWorkCandidate(
        Guid Id,
        bool CriticOnly,
        string SourceLanguageCode,
        string TargetLanguageCode);

    private async Task<LanguageProposalWorkCandidate?>
        SelectLanguageProposalCandidateAsync(
            int maximumAttempts,
            CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return await _db.Set<LegendCorpusCandidate>()
            .AsNoTracking()
            .Where(item =>
                (
                    (item.IsApproved &&
                     item.ProcessingState == "Queued") ||
                    (!item.IsApproved &&
                     (item.Provenance == MachineConversationProvenance ||
                      item.Provenance == ExternalObservationProvenance) &&
                     item.ProcessingState == "ConversationProposal")
                ) &&
                item.TeacherProposalAttemptCount < maximumAttempts &&
                (
                    item.TeacherProposalProcessingState == "Pending" ||
                    (
                        item.TeacherProposalProcessingState == "Processing" &&
                        item.TeacherProposalLeaseExpiresUtc != null &&
                        item.TeacherProposalLeaseExpiresUtc < now
                    )
                ))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedUtc)
            .Select(item => new LanguageProposalWorkCandidate(
                item.Id,
                !item.IsApproved &&
                    (item.Provenance == MachineConversationProvenance ||
                     item.Provenance == ExternalObservationProvenance) &&
                    item.ProcessingState ==
                        "ConversationProposal",
                item.SourceLanguageCode,
                item.TargetLanguageCode))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<LegendCorpusCandidate?>
        TryClaimLanguageProposalCandidateAsync(
            Guid candidateId,
            int maximumAttempts,
            CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(10);

        if (!_db.Database.IsRelational())
        {
            var candidate = await _db.Set<LegendCorpusCandidate>()
                .SingleOrDefaultAsync(
                    item =>
                        item.Id == candidateId &&
                        (
                            (item.IsApproved &&
                             item.ProcessingState == "Queued") ||
                            (!item.IsApproved &&
                             (item.Provenance == MachineConversationProvenance ||
                              item.Provenance == ExternalObservationProvenance) &&
                             item.ProcessingState == "ConversationProposal")
                        ) &&
                        item.TeacherProposalAttemptCount <
                            maximumAttempts &&
                        (
                            item.TeacherProposalProcessingState ==
                                "Pending" ||
                            (
                                item.TeacherProposalProcessingState ==
                                    "Processing" &&
                                item.TeacherProposalLeaseExpiresUtc !=
                                    null &&
                                item.TeacherProposalLeaseExpiresUtc <
                                    now
                            )
                        ),
                    cancellationToken);

            if (candidate is null)
                return null;

            candidate.TeacherProposalProcessingState = "Processing";
            candidate.TeacherProposalAttemptCount++;
            candidate.TeacherProposalLeaseExpiresUtc = expires;

            await _db.SaveChangesAsync(cancellationToken);
            return candidate;
        }

        var claimed = await _db.Set<LegendCorpusCandidate>()
            .Where(item =>
                item.Id == candidateId &&
                (
                    (item.IsApproved &&
                     item.ProcessingState == "Queued") ||
                    (!item.IsApproved &&
                     (item.Provenance == MachineConversationProvenance ||
                      item.Provenance == ExternalObservationProvenance) &&
                     item.ProcessingState == "ConversationProposal")
                ) &&
                item.TeacherProposalAttemptCount < maximumAttempts &&
                (
                    item.TeacherProposalProcessingState == "Pending" ||
                    (
                        item.TeacherProposalProcessingState == "Processing" &&
                        item.TeacherProposalLeaseExpiresUtc != null &&
                        item.TeacherProposalLeaseExpiresUtc < now
                    )
                ))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        item => item.TeacherProposalProcessingState,
                        "Processing")
                    .SetProperty(
                        item => item.TeacherProposalAttemptCount,
                        item => item.TeacherProposalAttemptCount + 1)
                    .SetProperty(
                        item => item.TeacherProposalLeaseExpiresUtc,
                        expires),
                cancellationToken);

        return claimed == 1
            ? await _db.Set<LegendCorpusCandidate>()
                .SingleAsync(
                    item => item.Id == candidateId,
                    cancellationToken)
            : null;
    }

    private async Task<bool>
        ProcessConversationMachineCritiqueAsync(
            LegendCorpusCandidate candidate,
            LegendLanguageTeacherProposalRequest request,
            LegendLanguageTeacherConfigurationPreflight preflight,
            int maximumAttempts,
            CancellationToken cancellationToken)
    {
        var proposal =
            await _db.Set<LegendLanguageTeacherProposal>()
                .SingleOrDefaultAsync(
                    item =>
                        item.CorpusCandidateId ==
                            candidate.Id &&
                        item.ValidationState ==
                            "AwaitingCritic" &&
                        item.Provenance ==
                            "MachineProposed",
                    cancellationToken);

        if (proposal is null)
        {
            candidate.TeacherProposalProcessingState =
                "Failed";
            candidate.TeacherProposalFailureCode =
                "conversation_machine_proposal_missing";
            candidate.TeacherProposalLeaseExpiresUtc =
                null;
            candidate.TeacherProposalProcessedUtc =
                DateTime.UtcNow;

            await _db.SaveChangesAsync(
                cancellationToken);

            return true;
        }

        LegendLanguageTeacherFamilyProposal? family;

        try
        {
            family =
                JsonSerializer.Deserialize<
                    LegendLanguageTeacherFamilyProposal>(
                    proposal.ProposalPayloadJson);
        }
        catch (JsonException)
        {
            family = null;
        }

        if (family is null)
        {
            proposal.ValidationState =
                "CriticRejected";
            proposal.CriticApproved =
                false;
            proposal.CriticReasonCodesJson =
                JsonSerializer.Serialize(
                    new[]
                    {
                        "conversation_machine_payload_invalid"
                    });
            proposal.UpdatedUtc =
                DateTime.UtcNow;

            candidate.TeacherProposalProcessingState =
                "CriticRejected";
            candidate.TeacherProposalFailureCode =
                "conversation_machine_payload_invalid";
            candidate.TeacherProposalLeaseExpiresUtc =
                null;
            candidate.TeacherProposalProcessedUtc =
                DateTime.UtcNow;

            await _db.SaveChangesAsync(
                cancellationToken);

            return true;
        }

        LegendLanguageTeacherCritiqueResult critique;

        try
        {
            critique =
                await _languageTeacher!.CritiqueAsync(
                    new LegendLanguageTeacherCritiqueRequest(
                        request,
                        family),
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await HandleLanguageTeacherFailureAsync(
                candidate,
                preflight,
                ClassifyLanguageTeacherException(exception),
                maximumAttempts,
                CancellationToken.None);

            return true;
        }

        if (!critique.Succeeded)
        {
            await HandleLanguageTeacherFailureAsync(
                candidate,
                preflight,
                NormalizeLanguageTeacherFailureCode(
                    critique.ErrorCode,
                    LegendLanguageTeacherFailureClassification.Provider),
                maximumAttempts,
                cancellationToken);

            return true;
        }

        await ResolveLanguageTeacherCircuitAsync(
            preflight,
            "ProviderRecovered",
            cancellationToken);

        proposal.CriticApproved =
            critique.Approved;

        proposal.CriticConfidence =
            critique.Confidence is null
                ? null
                : Math.Clamp(
                    critique.Confidence.Value,
                    0m,
                    1m);

        proposal.CriticReasonCodesJson =
            JsonSerializer.Serialize(
                critique.ReasonCodes);

        proposal.ValidationState =
            critique.Approved
                ? "AwaitingCanonicalValidation"
                : "CriticRejected";

        proposal.UpdatedUtc =
            DateTime.UtcNow;

        candidate.TeacherProposalProcessingState =
            proposal.ValidationState;

        candidate.TeacherProposalFailureCode =
            null;

        candidate.TeacherProposalLeaseExpiresUtc =
            null;

        candidate.TeacherProposalProcessedUtc =
            DateTime.UtcNow;

        await _db.SaveChangesAsync(
            cancellationToken);

        await RecordAsync(
            string.Equals(candidate.Provenance, ExternalObservationProvenance, StringComparison.Ordinal)
                ? "ResearchExternalObservationProposal"
                : "ConversationMachineProposal",
            "Info",
            proposal.ValidationState,
            candidate.SourceLanguageCode,
            candidate.PairKey(),
            null,
            string.Equals(candidate.Provenance, ExternalObservationProvenance, StringComparison.Ordinal)
                ? critique.Approved
                    ? "The retained ExternalObservation MachineProposed artifact survived the existing independent critic and now awaits the existing canonical novelty validator."
                    : "The retained ExternalObservation MachineProposed artifact was rejected by the existing independent critic and remains durable historical evidence."
                : critique.Approved
                    ? "The retained conversational MachineProposed artifact survived the existing independent critic and now awaits the existing canonical validator."
                    : "The retained conversational MachineProposed artifact was rejected by the existing independent critic and remains durable historical evidence.",
            cancellationToken);

        return true;
    }

    private async Task DeferLanguageProposalAsync(
        LegendCorpusCandidate candidate,
        string failureCode,
        int maximumAttempts,
        DateTime minimumLeaseExpiresUtc,
        CancellationToken cancellationToken)
    {
        candidate.TeacherProposalFailureCode =
            string.IsNullOrWhiteSpace(failureCode)
                ? LegendLanguageTeacherFailureClassification.Provider
                : failureCode[..Math.Min(failureCode.Length, 120)];

        if (candidate.TeacherProposalAttemptCount >= maximumAttempts)
        {
            candidate.TeacherProposalProcessingState = "Failed";
            candidate.TeacherProposalLeaseExpiresUtc = null;
            candidate.TeacherProposalProcessedUtc = DateTime.UtcNow;
        }
        else
        {
            candidate.TeacherProposalProcessingState = "Processing";
            var retryLease = DateTime.UtcNow.AddMinutes(
                    Math.Min(
                        30,
                        Math.Max(
                            5,
                            candidate.TeacherProposalAttemptCount * 5)));
            candidate.TeacherProposalLeaseExpiresUtc =
                retryLease > minimumLeaseExpiresUtc
                    ? retryLease
                    : minimumLeaseExpiresUtc;
            candidate.TeacherProposalProcessedUtc = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task DeferCandidateAsync(
        LegendCorpusCandidate candidate,
        string failureCode,
        CancellationToken cancellationToken)
    {
        candidate.AttemptCount++;
        candidate.FailureCode = failureCode;
        // Processing plus a future lease is the existing durable worker-claim
        // state. Reusing it prevents busy retries while allowing any instance
        // to reclaim work after the bounded lease; no secondary retry queue or
        // process-local timer becomes authoritative.
        candidate.ProcessingState = "Processing";
        candidate.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Max(5, candidate.AttemptCount * 5)));
        candidate.ProcessedUtc = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<LegendCorpusCandidate?> TryClaimCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(10);
        if (!_db.Database.IsRelational())
        {
            var candidate = await _db.Set<LegendCorpusCandidate>()
                .SingleOrDefaultAsync(item => item.Id == candidateId && item.IsApproved &&
                    (item.ProcessingState == "Pending" ||
                     (item.ProcessingState == "Processing" && item.LeaseExpiresUtc != null && item.LeaseExpiresUtc < now)), cancellationToken);
            if (candidate is null)
                return null;
            candidate.ProcessingState = "Processing";
            candidate.LeaseExpiresUtc = expires;
            await _db.SaveChangesAsync(cancellationToken);
            return candidate;
        }

        var claimed = await _db.Set<LegendCorpusCandidate>()
            .Where(item => item.Id == candidateId && item.IsApproved &&
                (item.ProcessingState == "Pending" ||
                 (item.ProcessingState == "Processing" && item.LeaseExpiresUtc != null && item.LeaseExpiresUtc < now)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProcessingState, "Processing")
                .SetProperty(item => item.LeaseExpiresUtc, expires), cancellationToken);
        return claimed == 1
            ? await _db.Set<LegendCorpusCandidate>().SingleAsync(item => item.Id == candidateId, cancellationToken)
            : null;
    }

    private Task RecordAsync(
        string category,
        string severity,
        string status,
        string? languageCode,
        string? pairKey,
        string? errorCode,
        string summary,
        CancellationToken cancellationToken) =>
        _operations?.TryRecordAsync(category, severity, status, languageCode, pairKey, errorCode, summary: summary, cancellationToken: cancellationToken)
        ?? Task.CompletedTask;
}

/// <summary>
/// Disabled-by-default runner for autonomous approved learning. The service
/// itself is generic and self-scheduling; configuration is the only gate.
/// </summary>
internal sealed class LegendConnectCorpusAcquisitionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LegendConnectCorpusAcquisitionHostedService> _logger;

    public LegendConnectCorpusAcquisitionHostedService(
        IServiceScopeFactory scopes,
        ILogger<LegendConnectCorpusAcquisitionHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ILegendConnectRuntimePolicyAuthority>()
                    .RecordWorkerHeartbeatAsync("Acquisition", stoppingToken);
                await scope.ServiceProvider.GetRequiredService<LegendConnectAutonomousLearningService>()
                    .ProcessOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Legend Connect corpus acquisition worker failed.");
            }
            // A durable Founder activation/pause must be observed promptly by
            // every instance without a restart or a second worker.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}

internal static class LegendCorpusCandidateExtensions
{
    internal static string PairKey(this LegendCorpusCandidate candidate) =>
        LegendLanguageIdentity.PairKey(candidate.SourceLanguageCode, candidate.TargetLanguageCode);
}
