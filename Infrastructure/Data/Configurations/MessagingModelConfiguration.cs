using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

internal static class MessagingModelConfiguration
{
    internal static void Configure(
        ModelBuilder modelBuilder,
        string? providerName)
    {
        ConfigureConversation(modelBuilder.Entity<MessageConversation>(), providerName);
        ConfigureParticipant(modelBuilder.Entity<MessageConversationParticipant>());
        ConfigureMessage(modelBuilder.Entity<InternalMessage>(), providerName);
        ConfigureAttachment(modelBuilder.Entity<MessageAttachment>());
        ConfigureVerificationReviewRequest(modelBuilder.Entity<VerificationReviewRequest>(), providerName);
        ConfigureControlledResourceGrant(modelBuilder.Entity<ControlledResourceGrant>(), providerName);
        ConfigureMessageTranslation(modelBuilder.Entity<MessageTranslation>());
        ConfigureLegendConnect(modelBuilder, providerName);
        ConfigureTranslationAccountUsage(modelBuilder, providerName);
        ConfigureMobileActivityNotification(modelBuilder.Entity<MobileActivityNotification>(), providerName);
        ConfigureUserGlobalBadge(modelBuilder.Entity<UserGlobalBadge>(), providerName);
        ConfigureMobilePushDevice(modelBuilder.Entity<MobilePushDevice>());
        ConfigureMobilePushDelivery(modelBuilder.Entity<MobilePushDelivery>());
        ConfigureGrant(modelBuilder.Entity<ClientAgentMessagingGrant>());
        ConfigureAuditEntry(modelBuilder.Entity<MessagingAuditEntry>());
    }

    private static void ConfigureConversation(
        EntityTypeBuilder<MessageConversation> entity,
        string? providerName)
    {
        entity.ToTable("MessageConversations");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.ConversationType)
            .IsRequired()
            .HasMaxLength(40);

        entity.Property(x => x.DirectConversationKey)
            .HasMaxLength(1_000);

        var directConversationKeyIndex = entity
            .HasIndex(x => x.DirectConversationKey)
            .IsUnique();

        if (IsSqlServer(providerName))
        {
            directConversationKeyIndex.HasFilter("[DirectConversationKey] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            directConversationKeyIndex.HasFilter("\"DirectConversationKey\" IS NOT NULL");
        }

        entity.Property(x => x.Subject)
            .HasMaxLength(240);

        entity.Property(x => x.GroupImageContentType)
            .HasMaxLength(150);

        entity.Property(x => x.Purpose)
            .HasMaxLength(40);

        entity.Property(x => x.CreatedByUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.OwnerParticipantType)
            .HasMaxLength(40);

        entity.Property(x => x.OwnerUserId)
            .HasMaxLength(450);

        entity.Property(x => x.HostUserId)
            .HasMaxLength(450);

        entity.Property(x => x.HostParticipantType)
            .HasMaxLength(40);

        entity.Property(x => x.MeetingLinkLabel)
            .HasMaxLength(100);

        entity.Property(x => x.MeetingLinkUrl)
            .HasMaxLength(2_048);

        entity.Property(x => x.MeetingFrequency)
            .HasMaxLength(24);

        entity.Property(x => x.MeetingWeekdays)
            .HasMaxLength(100);

        entity.Property(x => x.MeetingLocalTime)
            .HasMaxLength(5);

        entity.Property(x => x.MeetingTimeZoneId)
            .HasMaxLength(100);

        entity.Property(x => x.MeetingCustomDescription)
            .HasMaxLength(240);

        entity.HasIndex(x => new { x.IsPromoted, x.PromotionStartedUtc });

        entity.HasIndex(x => new { x.Purpose, x.CreatedByUserId, x.OwnerParticipantType });

        // There is exactly one staff-only verification review conversation for
        // the application. A filtered unique index makes that invariant hold
        // even when two members submit a request at the same moment.
        var verificationReviewIndex = entity
            .HasIndex(x => x.Purpose)
            .IsUnique();
        if (IsSqlServer(providerName))
        {
            verificationReviewIndex.HasFilter("[Purpose] = 'VerificationReview'");
        }
        else if (IsSqlite(providerName))
        {
            verificationReviewIndex.HasFilter("\"Purpose\" = 'VerificationReview'");
        }

        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);

        entity.HasIndex(x => x.LastMessageUtc);

        entity.HasIndex(x => x.IsClosed);
    }

    private static void ConfigureParticipant(
        EntityTypeBuilder<MessageConversationParticipant> entity)
    {
        entity.ToTable("MessageConversationParticipants");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.UserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.ParticipantType)
            .IsRequired()
            .HasMaxLength(40);

        entity.HasIndex(x => new { x.ConversationId, x.UserId, x.ParticipantType })
            .IsUnique();

        entity.HasIndex(x => new { x.UserId, x.IsActive });

        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.PinnedUtc });
        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.HiddenUtc });

        entity.HasOne(x => x.Conversation)
            .WithMany(x => x.Participants)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMessage(
        EntityTypeBuilder<InternalMessage> entity,
        string? providerName)
    {
        entity.ToTable("InternalMessages");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.SenderUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.SenderType)
            .IsRequired()
            .HasMaxLength(40);

        entity.Property(x => x.Body)
            .IsRequired()
            .HasMaxLength(10000);

        entity.Property(x => x.OriginalLanguage)
            .HasMaxLength(32);

        entity.Property(x => x.SenderPreferredLanguage)
            .HasMaxLength(32);

        entity.Property(x => x.ClientMessageId)
            .HasMaxLength(100);

        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);

        entity.HasIndex(x => new { x.ConversationId, x.SentUtc });

        entity.HasIndex(x => x.VerificationReviewRequestId);

        var clientMessageIndex = entity
            .HasIndex(x => x.ClientMessageId)
            .IsUnique();

        if (IsSqlServer(providerName))
        {
            clientMessageIndex.HasFilter("[ClientMessageId] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            clientMessageIndex.HasFilter("\"ClientMessageId\" IS NOT NULL");
        }

        entity.HasOne(x => x.Conversation)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureVerificationReviewRequest(
        EntityTypeBuilder<VerificationReviewRequest> entity,
        string? providerName)
    {
        entity.ToTable("VerificationReviewRequests");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.RequesterUserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.RequesterParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.ResourceType)
            .IsRequired()
            .HasMaxLength(80)
            .HasDefaultValue(ControlledResourceTypes.VerificationBadge);
        entity.Property(x => x.Status).IsRequired().HasMaxLength(24);
        entity.Property(x => x.ResolvedByUserId).HasMaxLength(450);
        entity.Property(x => x.ResolutionNote).HasMaxLength(1_000);
        entity.HasIndex(x => new { x.RequesterUserId, x.RequesterParticipantType, x.ResourceType, x.Status });
        entity.HasIndex(x => new { x.ReviewConversationId, x.RequestedUtc });

        var pendingRequestIndex = entity
            .HasIndex(x => new { x.RequesterUserId, x.RequesterParticipantType, x.ResourceType })
            .IsUnique();
        if (IsSqlServer(providerName))
        {
            pendingRequestIndex.HasFilter("[Status] = 'Pending'");
        }
        else if (IsSqlite(providerName))
        {
            pendingRequestIndex.HasFilter("\"Status\" = 'Pending'");
        }
    }

    private static void ConfigureControlledResourceGrant(
        EntityTypeBuilder<ControlledResourceGrant> entity,
        string? providerName)
    {
        entity.ToTable("ControlledResourceGrants");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.ResourceType).IsRequired().HasMaxLength(80);
        entity.Property(x => x.GrantedByUserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.RevokedByUserId).HasMaxLength(450);
        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);
        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.ResourceType }).IsUnique();
        entity.HasIndex(x => new { x.ResourceType, x.IsActive });
    }

    private static void ConfigureMessageTranslation(EntityTypeBuilder<MessageTranslation> entity)
    {
        entity.ToTable("MessageTranslations");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.TargetLanguage).IsRequired().HasMaxLength(32);
        entity.Property(x => x.TranslatedText).IsRequired().HasMaxLength(10_000);
        entity.Property(x => x.Provider).IsRequired().HasMaxLength(80);
        entity.HasIndex(x => new { x.InternalMessageId, x.TargetLanguage }).IsUnique();
        entity.HasOne(x => x.InternalMessage)
            .WithMany(x => x.Translations)
            .HasForeignKey(x => x.InternalMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureLegendConnect(ModelBuilder modelBuilder, string? providerName)
    {
        modelBuilder.Entity<LegendConnectModelTrainingRun>(entity =>
        {
            entity.ToTable("LegendConnectModelTrainingRuns");
            entity.HasKey(item => item.Id);

            entity.Property(item => item.RunKey)
                .IsRequired()
                .HasMaxLength(160);

            entity.Property(item => item.ScopeKey)
                .IsRequired()
                .HasMaxLength(80);

            entity.Property(item => item.DatasetIdentity)
                .IsRequired()
                .HasMaxLength(64);

            entity.Property(item => item.TrainingProvider)
                .IsRequired()
                .HasMaxLength(80);

            entity.Property(item => item.BaseModel)
                .IsRequired()
                .HasMaxLength(160);

            entity.Property(item => item.TrainingFileId)
                .HasMaxLength(240);

            entity.Property(item => item.ExternalJobId)
                .HasMaxLength(240);

            entity.Property(item => item.ChallengerModelVersion)
                .HasMaxLength(240);

            entity.Property(item => item.State)
                .IsRequired()
                .HasMaxLength(40);

            entity.Property(item => item.EvaluationState)
                .IsRequired()
                .HasMaxLength(40);

            entity.Property(item => item.PromotionState)
                .IsRequired()
                .HasMaxLength(40);

            entity.Property(item => item.HeldOutScore)
                .HasPrecision(9, 6);

            entity.Property(item => item.RegressionScore)
                .HasPrecision(9, 6);

            entity.Property(item => item.FailureCode)
                .HasMaxLength(120);

            entity.Property(item => item.FailureDetail)
                .HasMaxLength(1000);

            // RunKey is the cross-instance idempotency boundary.
            entity.HasIndex(item => item.RunKey)
                .IsUnique()
                .HasDatabaseName(
                    "IX_LegendConnectModelTrainingRuns_RunKey");

            // A generation is unique inside one logical model scope.
            entity.HasIndex(item => new
                {
                    item.ScopeKey,
                    item.Generation
                })
                .IsUnique()
                .HasDatabaseName(
                    "IX_LegendConnectModelTrainingRuns_ScopeGeneration");

            // Future canonical orchestration claims the next runnable record
            // from this index without introducing another queue.
            entity.HasIndex(item => new
                {
                    item.State,
                    item.LeaseExpiresUtc,
                    item.CreatedUtc
                })
                .HasDatabaseName(
                    "IX_LegendConnectModelTrainingRuns_Work");

            // Promotion/evaluation inspection remains bounded as history grows.
            entity.HasIndex(item => new
                {
                    item.ScopeKey,
                    item.PromotionState,
                    item.CreatedUtc
                })
                .HasDatabaseName(
                    "IX_LegendConnectModelTrainingRuns_Promotion");
        });

        modelBuilder.Entity<LegendLanguageDefinition>(entity =>
        {
            entity.ToTable("LegendLanguageDefinitions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.BaseLanguageCode).IsRequired().HasMaxLength(16);
            entity.Property(item => item.CanonicalName).IsRequired().HasMaxLength(120);
            entity.Property(item => item.NativeName).IsRequired().HasMaxLength(120);
            entity.Property(item => item.DatasetNamespace).IsRequired().HasMaxLength(80);
            entity.Property(item => item.StoragePartition).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => item.LanguageCode).IsUnique();
            entity.HasIndex(item => new { item.IsEnabled, item.IsTranslationEnabled });
            entity.HasIndex(item => item.StoragePartition).IsUnique();
        });

        modelBuilder.Entity<LegendLanguagePair>(entity =>
        {
            entity.ToTable("LegendLanguagePairs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.SourceLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.TargetLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.TranslationMemoryPartition).IsRequired().HasMaxLength(96);
            entity.Property(item => item.QualityState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.ActiveModelVersion).HasMaxLength(80);
            entity.Property(item => item.ProviderFallbackPolicy).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => item.PairKey).IsUnique();
            entity.HasIndex(item => new { item.SourceLanguageCode, item.TargetLanguageCode }).IsUnique();
            entity.HasIndex(item => item.TranslationMemoryPartition).IsUnique();
        });

        modelBuilder.Entity<LegendLanguageTextUnit>(entity =>
        {
            entity.ToTable("LegendLanguageTextUnits");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.StoragePartition).IsRequired().HasMaxLength(80);
            entity.Property(item => item.NormalizedHash).IsRequired().HasMaxLength(64);
            entity.Property(item => item.Text).IsRequired().HasMaxLength(10_000);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => new { item.LanguageCode, item.NormalizedHash }).IsUnique();
            entity.HasIndex(item => new { item.StoragePartition, item.CreatedUtc });
            entity.HasOne<LegendGlobalConcept>()
                .WithMany()
                .HasForeignKey(item => item.GlobalConceptId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LegendGlobalConcept>(entity =>
        {
            entity.ToTable("LegendGlobalConcepts");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ConceptKey).IsRequired().HasMaxLength(160);
            entity.Property(item => item.Category).HasMaxLength(80);
            entity.HasIndex(item => item.ConceptKey).IsUnique();
        });

        modelBuilder.Entity<LegendTranslationAlignment>(entity =>
        {
            entity.ToTable("LegendTranslationAlignments");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.Provider).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.Property(item => item.ProviderModel).HasMaxLength(120);
            entity.Property(item => item.QualityState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.Confidence).HasPrecision(5, 4);
            entity.HasIndex(item => item.SupersededByAlignmentId);
            entity.HasIndex(item => item.SupersededUtc);
            entity.HasIndex(item => new { item.PairKey, item.SourceTextUnitId, item.TargetTextUnitId }).IsUnique();
            entity.HasIndex(item => new { item.PairKey, item.QualityState });
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.SourceTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TargetTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendTranslationQualityEvidence>(entity =>
        {
            entity.ToTable("LegendTranslationQualityEvidence");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.Signal).IsRequired().HasMaxLength(40);
            entity.Property(item => item.ReasonCode).IsRequired().HasMaxLength(120);
            entity.Property(item => item.ResolutionState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.EvidenceIdentity).IsRequired().HasMaxLength(160);
            entity.Property(item => item.SemanticSignature).HasMaxLength(64);
            entity.HasIndex(item => item.EvidenceIdentity).IsUnique();
            entity.HasIndex(item => new { item.ObservedAlignmentId, item.ResolutionState, item.SupersededUtc });
            entity.HasIndex(item => new { item.PairKey, item.Signal, item.ResolutionState });
            entity.HasIndex(item => new { item.PairKey, item.SemanticSignature, item.Signal, item.SupersededUtc });
            entity.HasOne<LegendTranslationAlignment>()
                .WithMany()
                .HasForeignKey(item => item.ObservedAlignmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendTranslationAlignment>()
                .WithMany()
                .HasForeignKey(item => item.RelatedAlignmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageStructuralPattern>()
                .WithMany()
                .HasForeignKey(item => item.StructuralPatternId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageContextRelationship>()
                .WithMany()
                .HasForeignKey(item => item.ContextRelationshipId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.SourceTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TargetTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageContextRelationship>(entity =>
        {
            entity.ToTable("LegendLanguageContextRelationships");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).HasMaxLength(72);
            entity.Property(item => item.RelationshipKind).IsRequired().HasMaxLength(80);
            entity.Property(item => item.ContextSignature).IsRequired().HasMaxLength(320);
            entity.Property(item => item.SourcePatternSignature).IsRequired().HasMaxLength(1_000);
            entity.Property(item => item.ContextCategory).HasMaxLength(120);
            entity.Property(item => item.UsageRegister).HasMaxLength(80);
            entity.Property(item => item.RegionalVariant).HasMaxLength(80);
            entity.Property(item => item.Confidence).HasPrecision(5, 4);
            entity.Property(item => item.QualityState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => item.SupersededUtc);
            entity.HasIndex(item => new
            {
                item.PairKey,
                item.SourceTextUnitId,
                item.RelatedTextUnitId,
                item.RelationshipKind,
                item.ContextSignature
            }).IsUnique();
            entity.HasIndex(item => new { item.PairKey, item.SourcePatternSignature, item.QualityState });
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.SourceTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.RelatedTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendTranslationLearningEvent>(entity =>
        {
            entity.ToTable("LegendTranslationLearningEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.IdempotencyKey).IsRequired().HasMaxLength(180);
            entity.Property(item => item.SourceLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.TargetLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.SourceTextHash).IsRequired().HasMaxLength(64);
            entity.Property(item => item.TargetTextHash).IsRequired().HasMaxLength(64);
            entity.Property(item => item.SourceText).HasMaxLength(10_000);
            entity.Property(item => item.TargetText).HasMaxLength(10_000);
            entity.Property(item => item.Provider).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.Property(item => item.ContextCategory).HasMaxLength(120);
            entity.Property(item => item.EligibilityState).IsRequired().HasMaxLength(80);
            entity.Property(item => item.ProcessingState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.PromotionOutcome).HasMaxLength(40);
            entity.Property(item => item.FailureCode).HasMaxLength(80);
            entity.HasIndex(item => item.IdempotencyKey).IsUnique();
            entity.HasIndex(item => new { item.ProcessingState, item.EligibilityState, item.CreatedUtc });
            entity.HasIndex(item => item.PairKey);
        });

        modelBuilder.Entity<LegendCorpusCandidate>(entity =>
        {
            entity.ToTable("LegendCorpusCandidates");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.IdempotencyKey).IsRequired().HasMaxLength(180);
            entity.Property(item => item.SourceLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.TargetLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.SourceText).IsRequired().HasMaxLength(10_000);
            entity.Property(item => item.SourceTextHash).IsRequired().HasMaxLength(64);
            entity.Property(item => item.Category).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.Property(item => item.ProcessingState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.FailureCode).HasMaxLength(80);

            entity.Property(item => item.TeacherProposalProcessingState)
                .IsRequired()
                .HasMaxLength(40)
                .HasDefaultValue("NotStarted");
            entity.Property(item => item.TeacherProposalFailureCode).HasMaxLength(120);

            entity.HasIndex(item => item.IdempotencyKey).IsUnique();
            entity.HasIndex(item => new { item.IsApproved, item.ProcessingState, item.Priority, item.CreatedUtc });
            entity.HasIndex(item => new { item.SourceLanguageCode, item.TargetLanguageCode });
            entity.HasIndex(item => new { item.CurriculumFamilyId, item.SourceCurriculumExampleId });
            entity.HasIndex(item => new
            {
                item.IsApproved,
                item.ProcessingState,
                item.TeacherProposalProcessingState,
                item.TeacherProposalLeaseExpiresUtc,
                item.CreatedUtc
            });
            entity.HasOne<LegendCurriculumFamily>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumFamilyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.SourceCurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageTeacherProposal>(entity =>
        {
            entity.ToTable("LegendLanguageTeacherProposals");
            entity.HasKey(item => item.Id);

            entity.Property(item => item.ProposalIdentity)
                .IsRequired()
                .HasMaxLength(64);
            entity.Property(item => item.PairKey)
                .IsRequired()
                .HasMaxLength(72);
            entity.Property(item => item.SourceLanguageCode)
                .IsRequired()
                .HasMaxLength(32);
            entity.Property(item => item.TargetLanguageCode)
                .IsRequired()
                .HasMaxLength(32);
            entity.Property(item => item.EvidenceIdentityHash)
                .IsRequired()
                .HasMaxLength(64);
            entity.Property(item => item.FamilyKey)
                .IsRequired()
                .HasMaxLength(160);
            entity.Property(item => item.SemanticCategory)
                .IsRequired()
                .HasMaxLength(160);
            entity.Property(item => item.Rationale)
                .IsRequired()
                .HasMaxLength(2_000);
            entity.Property(item => item.Confidence)
                .HasPrecision(5, 4);
            entity.Property(item => item.ProposalPayloadJson)
                .IsRequired();
            entity.Property(item => item.CriticConfidence)
                .HasPrecision(5, 4);
            entity.Property(item => item.CriticReasonCodesJson)
                .IsRequired()
                .HasMaxLength(4_000);
            entity.Property(item => item.ValidationState)
                .IsRequired()
                .HasMaxLength(40);
            entity.Property(item => item.Provenance)
                .IsRequired()
                .HasMaxLength(80);
            entity.Property(item => item.CanonicalValidationFailureCode)
                .HasMaxLength(160);
            entity.Property(item => item.CurriculumAdmissionFailureCode)
                .HasMaxLength(160);

            entity.HasIndex(item => item.ProposalIdentity).IsUnique();
            entity.HasIndex(item => new
            {
                item.CorpusCandidateId,
                item.ValidationState,
                item.CreatedUtc
            });
            entity.HasIndex(item => new
            {
                item.ValidationState,
                item.CanonicalValidationLeaseExpiresUtc,
                item.CreatedUtc
            });
            entity.HasIndex(item => new
            {
                item.ValidationState,
                item.CurriculumAdmissionLeaseExpiresUtc,
                item.CreatedUtc
            });

            entity.HasOne<LegendCorpusCandidate>()
                .WithMany()
                .HasForeignKey(item => item.CorpusCandidateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendCurriculumFamily>(entity =>
        {
            entity.ToTable("LegendCurriculumFamilies");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FamilyKey).IsRequired().HasMaxLength(160);
            entity.Property(item => item.SemanticCategory).HasMaxLength(120);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => item.FamilyKey).IsUnique();
        });

        modelBuilder.Entity<LegendCurriculumExample>(entity =>
        {
            entity.ToTable("LegendCurriculumExamples");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => item.SupersededUtc);
            entity.HasIndex(item => new { item.CurriculumFamilyId, item.TextUnitId }).IsUnique();
            entity.HasIndex(item => new { item.CurriculumFamilyId, item.LanguageCode, item.UpdatedUtc });
            entity.HasIndex(item => item.DerivedFromCurriculumExampleId);
            entity.HasOne<LegendCurriculumFamily>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumFamilyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.DerivedFromCurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendCurriculumExampleVariation>(entity =>
        {
            entity.ToTable("LegendCurriculumExampleVariations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Dimension).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Value).IsRequired().HasMaxLength(160);
            entity.HasIndex(item => new { item.CurriculumExampleId, item.Dimension }).IsUnique();
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageStructuralPattern>(entity =>
        {
            entity.ToTable("LegendLanguageStructuralPatterns");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PropositionSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.VariationDimension).IsRequired().HasMaxLength(80);
            entity.Property(item => item.RealizationSignature).IsRequired().HasMaxLength(1_000);
            entity.Property(item => item.MaturityState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Confidence).HasPrecision(5, 4);
            entity.HasIndex(item => item.SupersededUtc);
            entity.HasIndex(item => new
            {
                item.PairKey,
                item.LanguageCode,
                item.VariationDimension,
                item.PropositionSignature
            }).IsUnique();
            entity.HasIndex(item => new { item.PairKey, item.LanguageCode, item.MaturityState, item.IsProductionEligible });
            entity.HasOne<LegendCurriculumFamily>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumFamilyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageStructuralRelationship>(entity =>
        {
            entity.ToTable("LegendLanguageStructuralRelationships");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.VariationDimension).IsRequired().HasMaxLength(80);
            entity.Property(item => item.RelationshipSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.AnchorLayoutSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.MaturityState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Confidence).HasPrecision(5, 4);
            entity.HasIndex(item => item.SupersededUtc);
            entity.HasIndex(item => new
            {
                item.PairKey,
                item.LanguageCode,
                item.VariationDimension,
                item.RelationshipSignature
            }).IsUnique();
            entity.HasIndex(item => new { item.PairKey, item.LanguageCode, item.MaturityState, item.IsProductionEligible });
        });

        modelBuilder.Entity<LegendFounderTrainingSubmission>(entity =>
        {
            entity.ToTable("LegendFounderTrainingSubmissions");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FounderUserId).HasMaxLength(450);
            entity.Property(item => item.SourceLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.RawText).IsRequired().HasMaxLength(30_000);
            entity.Property(item => item.RawTextHash).IsRequired().HasMaxLength(64);
            entity.Property(item => item.ContextCategory).HasMaxLength(120);
            entity.Property(item => item.UsageRegister).HasMaxLength(80);
            entity.Property(item => item.RegionalVariant).HasMaxLength(80);
            entity.Property(item => item.ProcessingState).IsRequired().HasMaxLength(40);
            entity.HasIndex(item => new { item.SourceLanguageCode, item.RawTextHash }).IsUnique();
            entity.HasIndex(item => new { item.ProcessingState, item.CreatedUtc });
            entity.HasIndex(item => item.LegacySourceTextUnitId).IsUnique();
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.LegacySourceTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendFounderTrainingSubmissionUnit>(entity =>
        {
            entity.ToTable("LegendFounderTrainingSubmissionUnits");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.UnitType).IsRequired().HasMaxLength(40);
            entity.HasIndex(item => new { item.SubmissionId, item.SequenceNumber }).IsUnique();
            entity.HasIndex(item => new { item.SubmissionId, item.TextUnitId }).IsUnique();
            entity.HasIndex(item => item.TextUnitId);
            entity.HasOne<LegendFounderTrainingSubmission>()
                .WithMany()
                .HasForeignKey(item => item.SubmissionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageStructuralEvidence>(entity =>
        {
            entity.ToTable("LegendLanguageStructuralEvidence");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.VariationDimension).IsRequired().HasMaxLength(80);
            entity.Property(item => item.BaselineVariationValue).IsRequired().HasMaxLength(160);
            entity.Property(item => item.ComparedVariationValue).IsRequired().HasMaxLength(160);
            entity.Property(item => item.EvidenceSignature).IsRequired().HasMaxLength(1_000);
            entity.Property(item => item.BaselineComponentSignature).IsRequired().HasMaxLength(1_000);
            entity.Property(item => item.ComparedComponentSignature).IsRequired().HasMaxLength(1_000);
            entity.Property(item => item.IndependentSourceIdentity).IsRequired().HasMaxLength(96);
            entity.Property(item => item.ContributionState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.StructuralRelationshipContributionState).HasMaxLength(40);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => item.SupersededUtc);
            entity.HasIndex(item => new
            {
                item.CurriculumFamilyId,
                item.PairKey,
                item.LanguageCode,
                item.VariationDimension,
                item.BaselineCurriculumExampleId,
                item.ComparedCurriculumExampleId
            }).IsUnique();
            entity.HasIndex(item => new { item.StructuralPatternId, item.ContributionState, item.SupersededUtc });
            entity.HasIndex(item => new
            {
                item.StructuralRelationshipId,
                item.StructuralRelationshipContributionState,
                item.SupersededUtc
            });
            entity.HasOne<LegendLanguageStructuralPattern>()
                .WithMany()
                .HasForeignKey(item => item.StructuralPatternId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageStructuralRelationship>()
                .WithMany()
                .HasForeignKey(item => item.StructuralRelationshipId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumFamily>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumFamilyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.BaselineCurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.ComparedCurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageLexeme>(entity =>
        {
            entity.ToTable("LegendLanguageLexemes");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.NormalizedHash).IsRequired().HasMaxLength(64);
            entity.Property(item => item.SurfaceForm).IsRequired().HasMaxLength(256);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => new { item.LanguageCode, item.NormalizedHash }).IsUnique();
        });

        modelBuilder.Entity<LegendLanguageLexicalOccurrence>(entity =>
        {
            entity.ToTable("LegendLanguageLexicalOccurrences");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.TextUnitId, item.TokenIndex }).IsUnique();
            entity.HasIndex(item => new { item.LexemeId, item.SupersededUtc });
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageLexeme>()
                .WithMany()
                .HasForeignKey(item => item.LexemeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageLexicalRelationship>(entity =>
        {
            entity.ToTable("LegendLanguageLexicalRelationships");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RelationshipKind).IsRequired().HasMaxLength(40);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => new { item.TextUnitId, item.SourceTokenIndex, item.RelatedTokenIndex }).IsUnique();
            entity.HasIndex(item => new { item.SourceLexemeId, item.RelatedLexemeId, item.SupersededUtc });
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageLexeme>()
                .WithMany()
                .HasForeignKey(item => item.SourceLexemeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageLexeme>()
                .WithMany()
                .HasForeignKey(item => item.RelatedLexemeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageCompositionalAnchor>(entity =>
        {
            entity.ToTable("LegendLanguageCompositionalAnchors");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.Dimension).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Value).IsRequired().HasMaxLength(160);
            entity.Property(item => item.PairKey).HasMaxLength(72);
            entity.Property(item => item.SemanticSignature).HasMaxLength(64);
            entity.Property(item => item.AnchorSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => new { item.CurriculumExampleId, item.AnchorSignature }).IsUnique();
            entity.HasIndex(item => new { item.LanguageCode, item.Dimension, item.Value, item.SupersededUtc });
            entity.HasIndex(item => new { item.SemanticSignature, item.SupersededUtc });
            entity.HasIndex(item => new { item.PairKey, item.SemanticSignature, item.SupersededUtc });
            entity.HasIndex(item => new { item.TextUnitId, item.SupersededUtc });
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageLexeme>()
                .WithMany()
                .HasForeignKey(item => item.LexemeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumFamily>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumFamilyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.CurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageTargetRealizationCandidate>(entity =>
        {
            entity.ToTable("LegendLanguageTargetRealizationCandidates");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            entity.Property(item => item.SourceLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.TargetLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.SemanticSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.VariationDimension).IsRequired().HasMaxLength(80);
            entity.Property(item => item.SemanticValue).IsRequired().HasMaxLength(160);
            entity.Property(item => item.TargetRealization).IsRequired().HasMaxLength(512);
            entity.Property(item => item.ContextSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.TemplateSignature).IsRequired().HasMaxLength(64);
            entity.Property(item => item.SlotSignature).IsRequired().HasMaxLength(128);
            entity.Property(item => item.CandidateIdentity).IsRequired().HasMaxLength(64);
            entity.Property(item => item.VerificationState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.MaturityState).IsRequired().HasMaxLength(40);
            entity.Property(item => item.Confidence).HasPrecision(9, 4);
            entity.Property(item => item.VerifiedByFounderUserId).HasMaxLength(256);
            entity.Property(item => item.RejectedByFounderUserId).HasMaxLength(256);
            entity.HasIndex(item => item.CandidateIdentity).IsUnique();
            entity.HasIndex(item => new { item.PairKey, item.SemanticSignature, item.ContextSignature, item.SupersededUtc });
            entity.HasIndex(item => new { item.VerificationState, item.MaturityState, item.SupersededUtc });
            entity.HasOne<LegendLanguageCompositionalAnchor>()
                .WithMany()
                .HasForeignKey(item => item.VerifiedAnchorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendLanguageTargetRealizationEvidence>(entity =>
        {
            entity.ToTable("LegendLanguageTargetRealizationEvidence");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EvidenceIdentity).IsRequired().HasMaxLength(64);
            entity.Property(item => item.Provenance).IsRequired().HasMaxLength(80);
            entity.HasIndex(item => new { item.CandidateId, item.EvidenceIdentity }).IsUnique();
            entity.HasIndex(item => new { item.SourceAlignmentId, item.SupersededUtc });
            entity.HasIndex(item => new { item.TargetCurriculumExampleId, item.SupersededUtc });
            entity.HasOne<LegendLanguageTargetRealizationCandidate>()
                .WithMany()
                .HasForeignKey(item => item.CandidateId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.SourceCurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendCurriculumExample>()
                .WithMany()
                .HasForeignKey(item => item.TargetCurriculumExampleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.SourceTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendLanguageTextUnit>()
                .WithMany()
                .HasForeignKey(item => item.TargetTextUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegendTranslationAlignment>()
                .WithMany()
                .HasForeignKey(item => item.SourceAlignmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegendTranslationProviderCapacity>(entity =>
        {
            entity.ToTable("LegendTranslationProviderCapacities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Provider).IsRequired().HasMaxLength(80);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => new { item.Provider, item.BillingPeriodStart }).IsUnique();
        });

        modelBuilder.Entity<LegendTranslationProviderReservation>(entity =>
        {
            entity.ToTable("LegendTranslationProviderReservations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Provider).IsRequired().HasMaxLength(80);
            entity.Property(item => item.ReservationReference).IsRequired().HasMaxLength(180);
            entity.Property(item => item.Purpose).IsRequired().HasMaxLength(20);
            entity.Property(item => item.State).IsRequired().HasMaxLength(20);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => new { item.Provider, item.ReservationReference }).IsUnique();
            entity.HasIndex(item => new { item.Provider, item.BillingPeriodStart, item.State, item.ReservationExpiresUtc });
        });

        modelBuilder.Entity<LegendTranslationPairDemand>(entity =>
        {
            entity.ToTable("LegendTranslationPairDemands");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.PairKey).IsRequired().HasMaxLength(72);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => item.PairKey).IsUnique();
            entity.HasIndex(item => item.LastRequestedUtc);
        });

        modelBuilder.Entity<LegendTranslationSystemUsage>(entity =>
        {
            entity.ToTable("LegendTranslationSystemUsages");
            entity.HasKey(item => item.Id);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => item.UsageDate).IsUnique();
        });

        modelBuilder.Entity<LegendConnectOperationalEvent>(entity =>
        {
            entity.ToTable("LegendConnectOperationalEvents");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Category).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Severity).IsRequired().HasMaxLength(20);
            entity.Property(item => item.Status).IsRequired().HasMaxLength(80);
            entity.Property(item => item.LanguageCode).HasMaxLength(32);
            entity.Property(item => item.PairKey).HasMaxLength(72);
            entity.Property(item => item.CorrelationId).HasMaxLength(128);
            entity.Property(item => item.ErrorCode).HasMaxLength(80);
            entity.Property(item => item.Summary).HasMaxLength(500);
            entity.HasIndex(item => new { item.PairKey, item.OccurredUtc });
            entity.HasIndex(item => new { item.LanguageCode, item.OccurredUtc });
            entity.HasIndex(item => new { item.Severity, item.IsResolved, item.OccurredUtc });
        });

        modelBuilder.Entity<LegendConnectKnowledgeAuditEntry>(entity =>
        {
            entity.ToTable("LegendConnectKnowledgeAuditEntries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FounderUserId).IsRequired().HasMaxLength(450);
            entity.Property(item => item.Action).IsRequired().HasMaxLength(80);
            entity.Property(item => item.Result).IsRequired().HasMaxLength(80);
            entity.Property(item => item.LanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.PairKey).HasMaxLength(72);
            entity.Property(item => item.Detail).HasMaxLength(500);
            entity.HasIndex(item => new { item.LanguageCode, item.OccurredUtc });
            entity.HasIndex(item => new { item.PairKey, item.OccurredUtc });
            entity.HasIndex(item => new { item.FounderUserId, item.OccurredUtc });
        });

        modelBuilder.Entity<LegendConnectRuntimePolicy>(entity =>
        {
            entity.ToTable("LegendConnectRuntimePolicies");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ScopeKey).IsRequired().HasMaxLength(40);
            entity.Property(item => item.ContextualCompositionMode).IsRequired().HasMaxLength(20);
            entity.Property(item => item.ContextualMinimumConfidence).HasPrecision(5, 4);
            entity.Property(item => item.LanguageIntelligenceReevaluationPhase).IsRequired().HasMaxLength(40);
            // Superseded single-target columns remain inert historical
            // storage. Runtime policy exposes no access path to them.
            entity.Property<string>("PriorityMode").IsRequired().HasMaxLength(40);
            entity.Property<string?>("PriorityLanguageCode").HasMaxLength(32);
            entity.Property<string?>("PriorityPairKey").HasMaxLength(72);
            entity.Property<string?>("PriorityLevel").HasMaxLength(40);
            entity.Property(item => item.UpdatedByUserId).HasMaxLength(450);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => item.ScopeKey).IsUnique();
        });

        modelBuilder.Entity<LegendConnectAutonomousLanguageFocus>(entity =>
        {
            entity.ToTable("LegendConnectAutonomousLanguageFocuses");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TargetLanguageCode).IsRequired().HasMaxLength(32);
            entity.HasIndex(item => new { item.RuntimePolicyId, item.TargetLanguageCode }).IsUnique();
            entity.HasOne<LegendConnectRuntimePolicy>()
                .WithMany()
                .HasForeignKey(item => item.RuntimePolicyId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTranslationAccountUsage(
        ModelBuilder modelBuilder,
        string? providerName)
    {
        modelBuilder.Entity<LegendTranslationEntitlement>(entity =>
        {
            entity.ToTable("LegendTranslationEntitlements");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.UserId).IsRequired().HasMaxLength(450);
            entity.Property(item => item.ParticipantType).IsRequired().HasMaxLength(40);
            entity.Property(item => item.EntitlementSource).IsRequired().HasMaxLength(80);
            entity.Property(item => item.UpdatedByUserId).HasMaxLength(450);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => new { item.UserId, item.ParticipantType }).IsUnique();
        });

        modelBuilder.Entity<LegendTranslationUsagePeriod>(entity =>
        {
            entity.ToTable("LegendTranslationUsagePeriods");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.UserId).IsRequired().HasMaxLength(450);
            entity.Property(item => item.ParticipantType).IsRequired().HasMaxLength(40);
            ConfigureRowVersion(entity.Property(item => item.RowVersion), providerName);
            entity.HasIndex(item => new { item.UserId, item.ParticipantType, item.PeriodStart }).IsUnique();
            entity.HasIndex(item => new { item.PeriodStart, item.ConsumedCharacters });
            entity.HasIndex(item => item.LastTranslationActivityUtc);
        });

        modelBuilder.Entity<LegendTranslationUsageLedger>(entity =>
        {
            entity.ToTable("LegendTranslationUsageLedgers");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RequestReference).IsRequired().HasMaxLength(64);
            entity.Property(item => item.UserId).IsRequired().HasMaxLength(450);
            entity.Property(item => item.ParticipantType).IsRequired().HasMaxLength(40);
            entity.Property(item => item.SourceLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.TargetLanguageCode).IsRequired().HasMaxLength(32);
            entity.Property(item => item.Provider).IsRequired().HasMaxLength(80);
            entity.Property(item => item.State).IsRequired().HasMaxLength(40);
            entity.Property(item => item.FailureCode).HasMaxLength(80);
            entity.HasIndex(item => item.RequestReference).IsUnique();
            entity.HasIndex(item => new { item.UserId, item.ParticipantType, item.PeriodStart, item.CreatedUtc });
            entity.HasIndex(item => new { item.PeriodStart, item.State });
        });
    }

    private static void ConfigureMobileActivityNotification(
        EntityTypeBuilder<MobileActivityNotification> entity,
        string? providerName)
    {
        entity.ToTable("MobileActivityNotifications");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.RecipientUserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.RecipientParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.Kind).IsRequired().HasMaxLength(80);
        entity.Property(x => x.Title).IsRequired().HasMaxLength(240);
        entity.Property(x => x.Detail).IsRequired().HasMaxLength(1_000);
        entity.Property(x => x.ConversationId);
        entity.Property(x => x.SourceMessageId);
        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);
        entity.HasIndex(x => new
        {
            x.RecipientUserId,
            x.RecipientParticipantType,
            x.OccurredUtc
        });
        var requestOutcomeIndex = entity.HasIndex(x => x.ControlledResourceRequestId).IsUnique();
        if (IsSqlServer(providerName))
        {
            requestOutcomeIndex.HasFilter("[ControlledResourceRequestId] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            requestOutcomeIndex.HasFilter("\"ControlledResourceRequestId\" IS NOT NULL");
        }

        var messageRecipientIndex = entity.HasIndex(x => new
        {
            x.SourceMessageId,
            x.RecipientUserId,
            x.RecipientParticipantType
        }).IsUnique();
        if (IsSqlServer(providerName))
        {
            messageRecipientIndex.HasFilter("[SourceMessageId] IS NOT NULL");
        }
        else if (IsSqlite(providerName))
        {
            messageRecipientIndex.HasFilter("\"SourceMessageId\" IS NOT NULL");
        }

        entity.HasIndex(x => new
        {
            x.RecipientUserId,
            x.RecipientParticipantType,
            x.IsRead,
            x.IsCleared,
            x.OccurredUtc
        });
        entity.HasIndex(x => new
        {
            x.RecipientUserId,
            x.RecipientParticipantType,
            x.ConversationId,
            x.IsRead,
            x.IsCleared
        });
    }

    private static void ConfigureUserGlobalBadge(
        EntityTypeBuilder<UserGlobalBadge> entity,
        string? providerName)
    {
        entity.ToTable("UserGlobalBadges");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
        ConfigureRowVersion(entity.Property(x => x.RowVersion), providerName);
        entity.HasIndex(x => new { x.UserId, x.ParticipantType }).IsUnique();
    }

    private static void ConfigureMobilePushDevice(EntityTypeBuilder<MobilePushDevice> entity)
    {
        entity.ToTable("MobilePushDevices");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserId).IsRequired().HasMaxLength(450);
        entity.Property(x => x.ParticipantType).IsRequired().HasMaxLength(40);
        entity.Property(x => x.DeviceToken).IsRequired().HasMaxLength(4_096);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
        entity.Property(x => x.Provider)
            .IsRequired()
            .HasMaxLength(16)
            // Existing rows are APNs registrations; this protects them during
            // the provider-scoped unique-index migration.
            .HasDefaultValue(MobilePushProviders.Apns);
        entity.Property(x => x.Environment).IsRequired().HasMaxLength(24);
        entity.HasIndex(x => new { x.Provider, x.TokenHash }).IsUnique();
        entity.HasIndex(x => new { x.UserId, x.ParticipantType, x.IsActive });
    }

    private static void ConfigureMobilePushDelivery(EntityTypeBuilder<MobilePushDelivery> entity)
    {
        entity.ToTable("MobilePushDeliveries");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.LastError).HasMaxLength(1_000);
        entity.HasIndex(x => new { x.NotificationId, x.MobilePushDeviceId }).IsUnique();
        entity.HasIndex(x => new { x.SentUtc, x.AbandonedUtc, x.NextAttemptUtc });
    }

    private static void ConfigureAttachment(
        EntityTypeBuilder<MessageAttachment> entity)
    {
        entity.ToTable("MessageAttachments");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.OriginalFileName)
            .IsRequired()
            .HasMaxLength(255);

        entity.Property(x => x.StoredFileName)
            .IsRequired()
            .HasMaxLength(255);

        entity.Property(x => x.ContentType)
            .IsRequired()
            .HasMaxLength(150);

        entity.Property(x => x.StoragePath)
            .IsRequired()
            .HasMaxLength(1000);

        entity.Property(x => x.ScanStatus)
            .IsRequired()
            .HasMaxLength(40);

        entity.HasIndex(x => x.InternalMessageId);

        entity.HasOne(x => x.InternalMessage)
            .WithMany(x => x.Attachments)
            .HasForeignKey(x => x.InternalMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureGrant(
        EntityTypeBuilder<ClientAgentMessagingGrant> entity)
    {
        entity.ToTable("ClientAgentMessagingGrants");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.ClientUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.AgentUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.GrantedByAgentUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.Reason)
            .HasMaxLength(1000);

        entity.HasIndex(x => new { x.ClientUserId, x.AgentUserId })
            .IsUnique();

        entity.HasIndex(x => new { x.ClientUserId, x.IsActive });

        entity.HasIndex(x => new { x.AgentUserId, x.IsActive });
    }

    private static void ConfigureAuditEntry(
        EntityTypeBuilder<MessagingAuditEntry> entity)
    {
        entity.ToTable("MessagingAuditEntries");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.ActorUserId)
            .IsRequired()
            .HasMaxLength(450);

        entity.Property(x => x.Action)
            .IsRequired()
            .HasMaxLength(100);

        entity.Property(x => x.TargetUserId)
            .HasMaxLength(450);

        entity.Property(x => x.Detail)
            .HasMaxLength(1000);

        entity.HasIndex(x => x.CreatedUtc);

        entity.HasIndex(x => x.ActorUserId);

        entity.HasIndex(x => x.ConversationId);
    }

    private static void ConfigureRowVersion(
        PropertyBuilder<byte[]> property,
        string? providerName)
    {
        if (IsSqlServer(providerName))
        {
            property.IsRowVersion();
            return;
        }

        property
            .IsConcurrencyToken()
            .ValueGeneratedNever();
    }

    private static bool IsSqlServer(string? providerName) =>
        providerName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSqlite(string? providerName) =>
        providerName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
}
