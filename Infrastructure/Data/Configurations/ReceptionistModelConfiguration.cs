using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

/// <summary>Additive shared reception coordination; all evidence relationships restrict deletion.</summary>
public static partial class ReceptionistModelConfiguration
{
    public const string IdentityCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder model, string? providerName)
    {
        // Identity columns use the provider's native binary comparison. SQL Server's
        // reviewed BIN2 schema is unchanged; SQLite must not depend on a registered alias.
        var identityCollation = providerName == "Microsoft.EntityFrameworkCore.Sqlite" ? "BINARY" : IdentityCollation;
        void Text(EntityTypeBuilder entity, string name, int maximum, bool required = true) =>
            entity.Property<string>(name).HasMaxLength(maximum).UseCollation(identityCollation).IsRequired(required);

        ConfigureManagement(model, identityCollation);
        var connection = model.Entity<MarketingConnection>();
        connection.Property(x => x.ReceptionistIngressKey).HasMaxLength(200).UseCollation(identityCollation);
        connection.HasIndex(x => new { x.Provider, x.ReceptionistIngressKey }).IsUnique()
            .HasFilter("[ReceptionistIngressKey] IS NOT NULL");
        connection.Property(x => x.ReceptionistProviderProjectKey).HasMaxLength(200).UseCollation(identityCollation);
        connection.HasIndex(x => new { x.Provider, x.ReceptionistProviderProjectKey }).IsUnique()
            .HasFilter("[ReceptionistProviderProjectKey] IS NOT NULL");

        var version = model.Entity<ReceptionistConfigurationVersion>();
        version.ToTable("ReceptionistConfigurationVersions", t => t.HasCheckConstraint("CK_ReceptionistConfigurationVersion_Version", "[Version] > 0"));
        version.HasKey(x => x.Id);
        version.HasAlternateKey(x => new { x.MarketingConnectionId, x.Id });
        version.HasIndex(x => new { x.MarketingConnectionId, x.Version }).IsUnique();
        version.Property(x => x.ConfigurationJson).IsRequired();
        Text(version, nameof(ReceptionistConfigurationVersion.ConfigurationSha256), 64);
        Text(version, nameof(ReceptionistConfigurationVersion.CreatedByActorId), 450);
        version.HasOne<MarketingConnection>().WithMany().HasForeignKey(x => x.MarketingConnectionId).OnDelete(DeleteBehavior.Restrict);
        connection.HasOne<ReceptionistConfigurationVersion>().WithMany()
            .HasForeignKey(x => new { x.Id, x.ReceptionistActiveConfigurationId })
            .HasPrincipalKey(x => new { x.MarketingConnectionId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var account = model.Entity<ReceptionistBudgetAccount>();
        account.ToTable("ReceptionistBudgetAccounts", t => t.HasCheckConstraint("CK_ReceptionistBudgetAccount_Kind", "[Kind] IN ('owner','funding')"));
        account.HasKey(x => x.Id);
        Text(account, nameof(ReceptionistBudgetAccount.Kind), 16);
        Text(account, nameof(ReceptionistBudgetAccount.ScopeKey), 200);
        Text(account, nameof(ReceptionistBudgetAccount.Currency), 3);
        Text(account, nameof(ReceptionistBudgetAccount.TimeZoneId), 200);
        account.HasIndex(x => new { x.Kind, x.ScopeKey }).IsUnique();
        account.Property(x => x.Revision).IsConcurrencyToken();

        var period = model.Entity<ReceptionistBudgetPeriod>();
        period.ToTable("ReceptionistBudgetPeriods", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistBudgetPeriod_Month", "[MonthKey] >= 101 AND [MonthKey] % 100 BETWEEN 1 AND 12 AND [StartUtc] < [EndUtc] AND [ReconciledThroughUtc] >= [StartUtc] AND [ReconciledThroughUtc] < [EndUtc]");
            t.HasCheckConstraint("CK_ReceptionistBudgetPeriod_Amounts", "[CashLimit] >= 0 AND [ConsumptionLimit] >= 0 AND [CashCommitted] >= 0 AND [ConsumptionCommitted] >= 0 AND [VoiceSecondsCommitted] >= 0 AND [OpeningCashCommitted] >= 0 AND [OpeningConsumptionCommitted] >= 0 AND [OpeningCashCeiling] >= 0 AND [OpeningConsumptionCeiling] >= 0 AND [OpeningVoiceSeconds] >= 0 AND [CashCommitted] >= [OpeningCashCommitted] AND [ConsumptionCommitted] >= [OpeningConsumptionCommitted] AND [VoiceSecondsCommitted] >= [OpeningVoiceSeconds]");
        });
        period.HasKey(x => x.Id);
        period.HasAlternateKey(x => new { x.BudgetAccountId, x.Id });
        period.HasIndex(x => new { x.BudgetAccountId, x.MonthKey }).IsUnique();
        period.HasOne<ReceptionistBudgetAccount>().WithMany().HasForeignKey(x => x.BudgetAccountId).OnDelete(DeleteBehavior.Restrict);
        Money(period, nameof(ReceptionistBudgetPeriod.CashLimit), nameof(ReceptionistBudgetPeriod.ConsumptionLimit),
            nameof(ReceptionistBudgetPeriod.CashCommitted), nameof(ReceptionistBudgetPeriod.ConsumptionCommitted),
            nameof(ReceptionistBudgetPeriod.OpeningCashCommitted), nameof(ReceptionistBudgetPeriod.OpeningConsumptionCommitted),
            nameof(ReceptionistBudgetPeriod.OpeningCashCeiling), nameof(ReceptionistBudgetPeriod.OpeningConsumptionCeiling));
        Text(period, nameof(ReceptionistBudgetPeriod.OpeningReceipt), 512);
        Text(period, nameof(ReceptionistBudgetPeriod.CeilingApprovalReceipt), 512, false);
        period.Property(x => x.Revision).IsConcurrencyToken();

        var call = model.Entity<ReceptionistCall>();
        call.ToTable("ReceptionistCalls", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistCall_State", "[State] IN ('reserved','accepted','uncertain','closed','rejected') AND (([ProviderClosedUtc] IS NULL AND [State] IN ('reserved','accepted','uncertain')) OR ([ProviderClosedUtc] IS NOT NULL AND [State] IN ('closed','rejected'))) AND ([VoiceUsageFinal] = 0 OR ([ProviderClosedUtc] IS NOT NULL AND [VoiceAllocationUncertain] = 0))");
            t.HasCheckConstraint("CK_ReceptionistCall_Duration", "[ReservedVoiceSeconds] > 0 AND [ObservedVoiceSeconds] >= 0 AND [MustStopUtc] >= [ReservedUtc]");
            t.HasCheckConstraint("CK_ReceptionistCall_WorkOwnership", "(([ProviderAcceptAttemptId] IS NULL AND [ProviderAcceptWorkerId] IS NULL AND [ProviderAcceptAttemptedUtc] IS NULL) OR ([ProviderAcceptAttemptId] IS NOT NULL AND [ProviderAcceptWorkerId] IS NOT NULL AND [ProviderAcceptAttemptedUtc] IS NOT NULL)) AND (([SidebandLeaseToken] IS NULL AND [SidebandWorkerId] IS NULL AND [SidebandLeaseExpiresUtc] IS NULL AND [SidebandFence] = 0 AND [SidebandObservationOnly] = 0) OR ([SidebandLeaseToken] IS NOT NULL AND [SidebandWorkerId] IS NOT NULL AND [SidebandLeaseExpiresUtc] IS NOT NULL AND [SidebandFence] > 0))");
        });
        call.HasKey(x => x.Id);
        call.HasAlternateKey(x => new { x.Id, x.OwnerBudgetAccountId });
        call.HasAlternateKey(x => new { x.Id, x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId });
        Text(call, nameof(ReceptionistCall.Provider), 20);
        Text(call, nameof(ReceptionistCall.IngressKey), 200);
        Text(call, nameof(ReceptionistCall.ProviderProjectKey), 200);
        Text(call, nameof(ReceptionistCall.ProviderSessionId), 200);
        Text(call, nameof(ReceptionistCall.State), 20);
        Text(call, nameof(ReceptionistCall.ProviderStateReceipt), 512, false);
        Text(call, nameof(ReceptionistCall.ProviderAcceptWorkerId), 100, false);
        Text(call, nameof(ReceptionistCall.SidebandWorkerId), 100, false);
        call.HasIndex(x => new { x.Provider, x.ProviderProjectKey, x.ProviderSessionId }).IsUnique();
        call.HasIndex(x => new { x.OwnerBudgetAccountId, x.ProviderClosedUtc });
        call.HasIndex(x => new { x.OwnerBudgetAccountId, x.VoiceUsageFinal });
        call.HasIndex(x => new { x.OwnerBudgetAccountId, x.VoiceAllocationUncertain });
        call.HasIndex(x => new { x.OwnerBudgetAccountId, x.ReservedUtc });
        call.HasIndex(x => new { x.ProviderClosedUtc, x.MustStopUtc });
        call.HasIndex(x => new { x.State, x.SidebandLeaseExpiresUtc });
        call.HasIndex(x => x.ProviderAcceptAttemptId).IsUnique().HasFilter("[ProviderAcceptAttemptId] IS NOT NULL");
        call.HasOne<ReceptionistConfigurationVersion>().WithMany().HasForeignKey(x => new { x.MarketingConnectionId, x.ConfigurationVersionId })
            .HasPrincipalKey(x => new { x.MarketingConnectionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        call.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        call.Property(x => x.Revision).IsConcurrencyToken();

        var decision = model.Entity<ReceptionistSessionDecision>();
        decision.ToTable("ReceptionistSessionDecisions", t => t.HasCheckConstraint("CK_ReceptionistSessionDecision_Outcome",
            "([Outcome] = 'denied' AND [CallId] IS NULL) OR ([Outcome] = 'reserved' AND [CallId] IS NOT NULL)"));
        decision.HasKey(x => x.Id);
        Text(decision, nameof(ReceptionistSessionDecision.Provider), 20);
        Text(decision, nameof(ReceptionistSessionDecision.ProviderProjectKey), 200);
        Text(decision, nameof(ReceptionistSessionDecision.ProviderSessionId), 200);
        Text(decision, nameof(ReceptionistSessionDecision.IngressKey), 200);
        Text(decision, nameof(ReceptionistSessionDecision.Outcome), 20);
        Text(decision, nameof(ReceptionistSessionDecision.Reason), 100);
        decision.HasIndex(x => new { x.Provider, x.ProviderProjectKey, x.ProviderSessionId }).IsUnique();
        decision.HasIndex(x => x.CallId).IsUnique().HasFilter("[CallId] IS NOT NULL");
        decision.HasOne<ReceptionistCall>().WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Restrict);

        var ingress = model.Entity<ReceptionistIngressEvent>();
        ingress.ToTable("ReceptionistIngressEvents");
        ingress.HasKey(x => x.Id);
        Text(ingress, nameof(ReceptionistIngressEvent.Provider), 20);
        Text(ingress, nameof(ReceptionistIngressEvent.ProviderProjectKey), 200);
        Text(ingress, nameof(ReceptionistIngressEvent.EventId), 200);
        ingress.HasIndex(x => new { x.Provider, x.ProviderProjectKey, x.EventId }).IsUnique();
        ingress.HasOne<ReceptionistSessionDecision>().WithMany().HasForeignKey(x => x.DecisionId).OnDelete(DeleteBehavior.Restrict);

        var cost = model.Entity<ReceptionistCostReservation>();
        cost.ToTable("ReceptionistCostReservations", t => t.HasCheckConstraint("CK_ReceptionistCostReservation_Amounts",
            "[CashReserved] >= 0 AND [ConsumptionReserved] >= 0 AND [CashObserved] >= 0 AND [ConsumptionObserved] >= 0 AND [OwnerBudgetAccountId] <> [FundingBudgetAccountId]"));
        cost.HasKey(x => x.Id);
        cost.HasAlternateKey(x => new { x.Id, x.OwnerBudgetAccountId, x.FundingBudgetAccountId });
        Text(cost, nameof(ReceptionistCostReservation.ComponentKey), 100);
        Money(cost, nameof(ReceptionistCostReservation.CashReserved), nameof(ReceptionistCostReservation.ConsumptionReserved),
            nameof(ReceptionistCostReservation.CashObserved), nameof(ReceptionistCostReservation.ConsumptionObserved));
        cost.HasIndex(x => new { x.CallId, x.ComponentKey }).IsUnique();
        cost.HasIndex(x => new { x.OwnerBudgetAccountId, x.CashFinal, x.ConsumptionFinal });
        cost.HasIndex(x => new { x.FundingBudgetAccountId, x.CashFinal, x.ConsumptionFinal });
        cost.HasOne<ReceptionistCall>().WithMany().HasForeignKey(x => new { x.CallId, x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId })
            .HasPrincipalKey(x => new { x.Id, x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId }).OnDelete(DeleteBehavior.Restrict);
        cost.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.FundingBudgetAccountId, x.FundingBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        cost.Property(x => x.Revision).IsConcurrencyToken();

        var posting = model.Entity<ReceptionistCostPosting>();
        posting.ToTable("ReceptionistCostPostings", t => t.HasCheckConstraint("CK_ReceptionistCostPosting_Amounts", "[Kind] IN ('cash','consumption') AND [CumulativeAmount] >= 0 AND [OwnerBudgetAccountId] <> [FundingBudgetAccountId]"));
        posting.HasKey(x => x.Id);
        Text(posting, nameof(ReceptionistCostPosting.Kind), 16);
        Money(posting, nameof(ReceptionistCostPosting.CumulativeAmount));
        posting.HasIndex(x => new { x.CostReservationId, x.Kind, x.OwnerBudgetPeriodId, x.FundingBudgetPeriodId }).IsUnique();
        posting.HasOne<ReceptionistCostReservation>().WithMany().HasForeignKey(x => new { x.CostReservationId, x.OwnerBudgetAccountId, x.FundingBudgetAccountId })
            .HasPrincipalKey(x => new { x.Id, x.OwnerBudgetAccountId, x.FundingBudgetAccountId }).OnDelete(DeleteBehavior.Restrict);
        posting.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        posting.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.FundingBudgetAccountId, x.FundingBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        posting.Property(x => x.Revision).IsConcurrencyToken();

        var voice = model.Entity<ReceptionistVoiceUsagePosting>();
        voice.ToTable("ReceptionistVoiceUsagePostings", t => t.HasCheckConstraint("CK_ReceptionistVoiceUsagePosting_Seconds", "[CumulativeSeconds] >= 0"));
        voice.HasKey(x => x.Id);
        voice.HasIndex(x => new { x.CallId, x.OwnerBudgetPeriodId }).IsUnique();
        voice.HasOne<ReceptionistCall>().WithMany().HasForeignKey(x => new { x.CallId, x.OwnerBudgetAccountId })
            .HasPrincipalKey(x => new { x.Id, x.OwnerBudgetAccountId }).OnDelete(DeleteBehavior.Restrict);
        voice.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.OwnerBudgetAccountId, x.OwnerBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        voice.Property(x => x.Revision).IsConcurrencyToken();

        var charge = model.Entity<ReceptionistAccountCharge>();
        charge.ToTable("ReceptionistAccountCharges", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistAccountCharge_Amounts", "[CashObserved] >= 0 AND [ConsumptionObserved] >= 0 AND [OwnerBudgetAccountId] <> [FundingBudgetAccountId]");
            t.HasCheckConstraint("CK_ReceptionistAccountCharge_Dates", "(([CashDebitedUtc] IS NULL AND [CashOwnerBudgetPeriodId] IS NULL AND [CashFundingBudgetPeriodId] IS NULL AND CAST([CashObserved] AS decimal(20,6)) = 0) OR ([CashDebitedUtc] IS NOT NULL AND [CashOwnerBudgetPeriodId] IS NOT NULL AND [CashFundingBudgetPeriodId] IS NOT NULL)) AND (([ConsumptionIncurredUtc] IS NULL AND [ConsumptionOwnerBudgetPeriodId] IS NULL AND [ConsumptionFundingBudgetPeriodId] IS NULL AND CAST([ConsumptionObserved] AS decimal(20,6)) = 0) OR ([ConsumptionIncurredUtc] IS NOT NULL AND [ConsumptionOwnerBudgetPeriodId] IS NOT NULL AND [ConsumptionFundingBudgetPeriodId] IS NOT NULL))");
        });
        charge.HasKey(x => x.Id);
        Text(charge, nameof(ReceptionistAccountCharge.ProviderChargeKey), 200);
        Text(charge, nameof(ReceptionistAccountCharge.ComponentKey), 100);
        Money(charge, nameof(ReceptionistAccountCharge.CashObserved), nameof(ReceptionistAccountCharge.ConsumptionObserved));
        charge.HasIndex(x => new { x.FundingBudgetAccountId, x.ProviderChargeKey }).IsUnique();
        charge.HasOne<ReceptionistBudgetAccount>().WithMany().HasForeignKey(x => x.OwnerBudgetAccountId).OnDelete(DeleteBehavior.Restrict);
        charge.HasOne<ReceptionistBudgetAccount>().WithMany().HasForeignKey(x => x.FundingBudgetAccountId).OnDelete(DeleteBehavior.Restrict);
        charge.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.OwnerBudgetAccountId, x.CashOwnerBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        charge.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.FundingBudgetAccountId, x.CashFundingBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        charge.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.OwnerBudgetAccountId, x.ConsumptionOwnerBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        charge.HasOne<ReceptionistBudgetPeriod>().WithMany().HasForeignKey(x => new { x.FundingBudgetAccountId, x.ConsumptionFundingBudgetPeriodId })
            .HasPrincipalKey(x => new { x.BudgetAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        charge.Property(x => x.Revision).IsConcurrencyToken();

        var receipt = model.Entity<ReceptionistUsageReceipt>();
        receipt.ToTable("ReceptionistUsageReceipts", t => t.HasCheckConstraint("CK_ReceptionistUsageReceipt_Target",
            "([CallId] IS NOT NULL AND [AccountChargeId] IS NULL) OR ([CallId] IS NULL AND [AccountChargeId] IS NOT NULL)"));
        receipt.HasKey(x => x.Id);
        Text(receipt, nameof(ReceptionistUsageReceipt.ScopeKey), 64);
        Text(receipt, nameof(ReceptionistUsageReceipt.ComponentKey), 100);
        Text(receipt, nameof(ReceptionistUsageReceipt.ProviderReceiptKey), 200);
        Text(receipt, nameof(ReceptionistUsageReceipt.PayloadSha256), 64);
        receipt.HasIndex(x => new { x.ScopeKey, x.ProviderReceiptKey }).IsUnique();
        receipt.HasOne<ReceptionistCall>().WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<ReceptionistAccountCharge>().WithMany().HasForeignKey(x => x.AccountChargeId).OnDelete(DeleteBehavior.Restrict);

        var action = model.Entity<ReceptionistActionIntent>();
        action.ToTable("ReceptionistActionIntents", t =>
        {
            t.HasCheckConstraint("CK_ReceptionistActionIntent_State", "[State] IN ('pending','uncertain','verified','rejected') AND ([State] <> 'verified' OR ([ActualRecordReference] IS NOT NULL AND [VerificationReceipt] IS NOT NULL))");
            t.HasCheckConstraint("CK_ReceptionistActionIntent_Attempt", "[AttemptNumber] > 0 AND [ExecutionFence] >= 0 AND ([ExecutionLeaseToken] IS NULL OR ([ExecutionFence] > 0 AND [WorkerId] IS NOT NULL))");
        });
        action.HasKey(x => x.Id);
        Text(action, nameof(ReceptionistActionIntent.LogicalActionKey), 160);
        Text(action, nameof(ReceptionistActionIntent.CapabilityKey), 100);
        Text(action, nameof(ReceptionistActionIntent.RouteKey), 160);
        Text(action, nameof(ReceptionistActionIntent.ArgumentsSha256), 64);
        Text(action, nameof(ReceptionistActionIntent.ConfirmationReceipt), 512);
        Text(action, nameof(ReceptionistActionIntent.WorkerId), 100, false);
        Text(action, nameof(ReceptionistActionIntent.State), 20);
        Text(action, nameof(ReceptionistActionIntent.ActualRecordReference), 512, false);
        Text(action, nameof(ReceptionistActionIntent.VerificationReceipt), 512, false);
        action.HasIndex(x => new { x.CallId, x.LogicalActionKey }).IsUnique();
        action.HasIndex(x => x.AttemptId).IsUnique();
        action.HasOne<ReceptionistCall>().WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Restrict);
        action.Property(x => x.Revision).IsConcurrencyToken();
    }

    private static void Money(EntityTypeBuilder entity, params string[] names)
    {
        foreach (var name in names) entity.Property<decimal>(name).HasPrecision(20, 6);
    }
}
