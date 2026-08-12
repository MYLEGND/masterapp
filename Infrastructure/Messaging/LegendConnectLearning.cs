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

    public LegendConnectCorpusService(
        MasterAppDbContext db,
        ILegendLanguageRegistry languages,
        ILogger<LegendConnectCorpusService> logger,
        ILegendConnectOperationalEventWriter? operations = null)
    {
        _db = db;
        _languages = languages;
        _logger = logger;
        _operations = operations;
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
            pairEntity.CorpusCoverage = await _db.Set<LegendTranslationAlignment>()
                .CountAsync(candidate => candidate.PairKey == pair.PairKey && candidate.SupersededUtc == null, cancellationToken);
            if (alignment.HumanVerified)
                pairEntity.QualityState = "Validated";
            pairEntity.UpdatedUtc = DateTime.UtcNow;

            item.ProcessingState = "Processed";
            item.ProcessedUtc = DateTime.UtcNow;
            item.LeaseExpiresUtc = null;
            item.FailureCode = null;
            item.PromotionOutcome = alignmentCreated ? "Promoted" : "Reused";
            await _db.SaveChangesAsync(cancellationToken);
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
    /// Founder-approved knowledge enters the very same isolated text-unit,
    /// alignment, and contextual-intelligence pipeline as approved automated
    /// corpus work. Exact canonical duplicates are rejected before any insert
    /// and enforced again by the database uniqueness boundary.
    /// </summary>
    internal async Task<LegendConnectKnowledgeSubmissionResult> SubmitApprovedKnowledgeAsync(
        LegendConnectKnowledgeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? reusableSourceTextUnitId = null)
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
        if (reusableSource is null && await _db.Set<LegendLanguageTextUnit>().AnyAsync(item =>
                item.LanguageCode == sourceLanguage && item.NormalizedHash == sourceHash, cancellationToken))
        {
            return Duplicate(sourceLanguage, targetLanguage, "This exact entry already exists in this language.");
        }
        if (targetLanguage is not null && targetHash is not null &&
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
            var source = reusableSource ?? NewTextUnit(sourceLanguage, sourceText, sourceHash, submission.Provenance);
            if (reusableSource is null)
                _db.Set<LegendLanguageTextUnit>().Add(source);

            LegendLanguageTextUnit? target = null;
            LegendTranslationAlignment? alignment = null;
            string? pairKey = null;
            if (targetLanguage is not null && targetText is not null && targetHash is not null)
            {
                target = NewTextUnit(targetLanguage, targetText, targetHash, submission.Provenance);
                _db.Set<LegendLanguageTextUnit>().Add(target);
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
                    cancellationToken);
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
                    cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            if (targetLanguage is null)
            {
                // A monolingual Founder-approved entry is an approved seed,
                // not a terminal dead end. Project it into the existing
                // candidate authority for valid enabled targets so the
                // existing planner/worker can expand only missing knowledge
                // under its normal capacity, quality, and deduplication gates.
                await QueueFounderSeedCandidatesAsync(source, cancellationToken);
            }
            if (pairKey is not null)
            {
                var pairEntity = await _db.Set<LegendLanguagePair>()
                    .SingleAsync(item => item.PairKey == pairKey, cancellationToken);
                pairEntity.CorpusCoverage = await _db.Set<LegendTranslationAlignment>()
                    .CountAsync(item => item.PairKey == pairKey && item.SupersededUtc == null, cancellationToken);
                pairEntity.QualityState = "Validated";
                pairEntity.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
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
        var existing = await _db.Set<LegendLanguageTextUnit>()
            .Where(item => item.LanguageCode == sourceLanguage && hashes.Contains(item.NormalizedHash))
            .ToDictionaryAsync(item => item.NormalizedHash, StringComparer.Ordinal, cancellationToken);
        var createdCount = 0;
        foreach (var item in normalizedByHash)
        {
            if (existing.ContainsKey(item.Hash))
                continue;
            var created = NewTextUnit(sourceLanguage, item.Text, item.Hash, "FounderApproved");
            _db.Set<LegendLanguageTextUnit>().Add(created);
            existing[item.Hash] = created;
            createdCount++;
        }
        if (createdCount > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var textUnits = normalizedByHash.ToDictionary(item => item.Hash, item => existing[item.Hash], StringComparer.Ordinal);
        var textUnitIds = textUnits.Values.Select(item => item.Id).ToArray();
        var contextSignature = LegendLanguageIdentity.ContextSignature(contextCategory, usageRegister, regionalVariant);
        var existingContexts = await _db.Set<LegendLanguageContextRelationship>()
            .Where(item => item.PairKey == null && textUnitIds.Contains(item.SourceTextUnitId) &&
                item.SourceTextUnitId == item.RelatedTextUnitId &&
                item.RelationshipKind == "ContextualExample" &&
                item.ContextSignature == contextSignature && item.SupersededUtc == null)
            .Select(item => item.SourceTextUnitId)
            .ToListAsync(cancellationToken);
        var contextIds = existingContexts.ToHashSet();
        foreach (var source in textUnits.Values)
        {
            if (contextIds.Contains(source.Id))
                continue;
            _db.Set<LegendLanguageContextRelationship>().Add(new LegendLanguageContextRelationship
            {
                Id = Guid.NewGuid(),
                PairKey = null,
                SourceTextUnitId = source.Id,
                RelatedTextUnitId = source.Id,
                RelationshipKind = "ContextualExample",
                ContextSignature = contextSignature,
                SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(source.Text),
                ContextCategory = NormalizeOptional(contextCategory, 120),
                UsageRegister = NormalizeOptional(usageRegister, 80),
                RegionalVariant = NormalizeOptional(regionalVariant, 80),
                Confidence = 1m,
                QualityState = "Verified",
                Provenance = "FounderApproved",
                ObservationCount = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }

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
            : (await _db.Set<LegendTranslationAlignment>()
                .Where(item => pairKeys.Contains(item.PairKey) && textUnitIds.Contains(item.SourceTextUnitId) && item.SupersededUtc == null)
                .Select(item => new { item.PairKey, item.SourceTextUnitId })
                .ToListAsync(cancellationToken))
                .Select(item => (item.PairKey, item.SourceTextUnitId))
                .ToHashSet();
        var sourceHashes = textUnits.Keys.ToArray();
        var targetCodes = pairs.Select(item => item.TargetLanguageCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existingCandidateKeys = targetCodes.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _db.Set<LegendCorpusCandidate>()
                .Where(item => item.SourceLanguageCode == sourceLanguage &&
                    targetCodes.Contains(item.TargetLanguageCode) && sourceHashes.Contains(item.SourceTextHash))
                .Select(item => item.IdempotencyKey)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        var queued = 0;
        foreach (var source in textUnits.Values)
        {
            foreach (var pair in pairs)
            {
                if (activeAlignments.Contains((pair.PairKey, source.Id)))
                    continue;
                var idempotencyKey = $"founder-seed:{source.Id:D}:{pair.PairKey}";
                if (!existingCandidateKeys.Add(idempotencyKey))
                    continue;
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
        CancellationToken cancellationToken = default) =>
        QueueFounderSeedCandidatesAsync(
            source,
            cancellationToken,
            curriculumFamilyId,
            sourceCurriculumExampleId);

    private async Task QueueFounderSeedCandidatesAsync(
        LegendLanguageTextUnit source,
        CancellationToken cancellationToken,
        Guid? curriculumFamilyId = null,
        Guid? sourceCurriculumExampleId = null)
    {
        if (!source.IsTrainingEligible)
            return;
        var enabledTargets = await _languages.ListEnabledTranslationLanguagesAsync(cancellationToken);
        var pending = false;
        foreach (var target in enabledTargets.Where(item => item.IsLearningEnabled && item.IsTranslationEnabled))
        {
            if (string.Equals(source.LanguageCode, target.Code, StringComparison.OrdinalIgnoreCase))
                continue;
            var pair = await _languages.GetOrCreateEnabledPairAsync(source.LanguageCode, target.Code, cancellationToken);
            if (pair is null || string.Equals(pair.SourceLanguageCode, pair.TargetLanguageCode, StringComparison.OrdinalIgnoreCase))
                continue;

            var idempotencyKey = $"founder-seed:{source.Id:D}:{pair.PairKey}";
            var aligned = await _db.Set<LegendTranslationAlignment>().AnyAsync(item =>
                item.PairKey == pair.PairKey && item.SourceTextUnitId == source.Id && item.SupersededUtc == null,
                cancellationToken);
            if (aligned)
                continue;

            var existingCandidate = await _db.Set<LegendCorpusCandidate>()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existingCandidate is not null)
            {
                // A single-entry Founder seed may have already created this
                // canonical candidate. Tag the same candidate when it later
                // becomes a curriculum example rather than duplicating an
                // Azure request or a confidence observation.
                if (curriculumFamilyId is not null &&
                    existingCandidate.CurriculumFamilyId is null &&
                    existingCandidate.ProcessingState is "Pending" or "Processing")
                {
                    existingCandidate.CurriculumFamilyId = curriculumFamilyId;
                    existingCandidate.SourceCurriculumExampleId = sourceCurriculumExampleId;
                    pending = true;
                }
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
                CurriculumFamilyId = curriculumFamilyId,
                SourceCurriculumExampleId = sourceCurriculumExampleId,
                IsApproved = true,
                ProcessingState = "Pending",
                CreatedUtc = DateTime.UtcNow
            });
            pending = true;
        }

        if (!pending)
            return;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<LegendLanguageTextUnit> GetOrCreateTextUnitAsync(
        string languageCode,
        string text,
        string hash,
        string provenance,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Set<LegendLanguageTextUnit>()
            .SingleOrDefaultAsync(item => item.LanguageCode == languageCode && item.NormalizedHash == hash, cancellationToken);
        if (existing is not null)
            return existing;

        var created = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = languageCode,
            StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
            NormalizedHash = hash,
            Text = LegendLanguageIdentity.NormalizeText(text),
            Provenance = provenance,
            IsTrainingEligible = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _db.Set<LegendLanguageTextUnit>().Add(created);
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
        CancellationToken cancellationToken)
    {
        var contextSignature = LegendLanguageIdentity.ContextSignature(
            contextCategory,
            usageRegister,
            regionalVariant);
        var existing = await _db.Set<LegendLanguageContextRelationship>()
            .SingleOrDefaultAsync(item => item.PairKey == pairKey &&
                item.SourceTextUnitId == source.Id &&
                item.RelatedTextUnitId == related.Id &&
                item.RelationshipKind == "ContextualExample" &&
                item.ContextSignature == contextSignature && item.SupersededUtc == null,
                cancellationToken);
        if (existing is not null)
        {
            existing.ObservationCount++;
            existing.Confidence = Math.Max(existing.Confidence, confidence);
            if (qualityState == "Verified")
                existing.QualityState = "Verified";
            existing.UpdatedUtc = DateTime.UtcNow;
            return existing;
        }

        var created = new LegendLanguageContextRelationship
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
        _db.Set<LegendLanguageContextRelationship>().Add(created);
        return created;
    }

    private static LegendLanguageTextUnit NewTextUnit(
        string languageCode,
        string normalizedText,
        string hash,
        string provenance) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = hash,
        Text = normalizedText,
        Provenance = NormalizeRequired(provenance, 80),
        IsTrainingEligible = true,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

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

internal sealed class LegendConnectLearningHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LegendConnectLearningHostedService> _logger;

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
                using var scope = _scopes.CreateScope();
                var runtime = scope.ServiceProvider.GetRequiredService<ILegendConnectRuntimePolicyAuthority>();
                await runtime.RecordWorkerHeartbeatAsync("Learning", stoppingToken);
                await scope.ServiceProvider
                    .GetRequiredService<LegendConnectFounderTrainingIngestionAuthority>()
                    .ReconcileLegacyAsync(25, stoppingToken);
                if ((await runtime.GetEffectiveAsync(stoppingToken)).LearningEnabled)
                {
                    await scope.ServiceProvider.GetRequiredService<LegendConnectCorpusService>()
                        .ProcessPendingAsync(25, stoppingToken);
                }
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
        var candidates = await _db.Set<LegendCorpusCandidate>()
            .Where(item => item.IsApproved &&
                (item.ProcessingState == "Pending" ||
                 (item.ProcessingState == "Processing" && item.LeaseExpiresUtc != null && item.LeaseExpiresUtc < now)))
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
            .ToDictionaryAsync(item => item.PairKey, item => item.TranslationRequestCount, StringComparer.OrdinalIgnoreCase, cancellationToken);

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
        LegendConnectCurriculumService? curriculum = null)
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
    }

    internal bool IsBootstrapEnabled =>
        _configuration.GetValue<bool>("LegendConnect:CorpusAcquisition:Enabled") &&
        (_configuration.GetValue<long?>("LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters") ?? 0) > 0;

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
        try
        {
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
            await _capacity.CompleteAsync(reservation, providerSucceeded, cancellationToken);
        }
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
