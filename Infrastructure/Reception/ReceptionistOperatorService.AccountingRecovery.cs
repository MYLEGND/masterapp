using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistOperatorService
{
    // Typed Founder preparation only. It cannot create an account, increase ceilings, or invent an opening.
    public async Task<ReceptionistPreparedAccounting> PrepareSavedAccountingAsync(ReceptionistAccountingDraft input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var actor = RequireFounder();
        if (input.Operation is not ("open_budget_period" or "set_budget_admission"))
            throw new ArgumentException("Choose a current opening or accounting permission operation.");
        var observed = await admission.ReadAccountingPreparationAsync(input.ExpectedOwnerKey, input.ConfigurationId, ct);
        var account = observed.Accounts.SingleOrDefault(x => x.Account.Id == input.BudgetAccountId)
            ?? throw new InvalidOperationException("Choose an existing accounting account referenced by this current configuration.");
        if (input.ExpectedConnectionRevision != observed.ConnectionRevision || input.ExpectedAccountRevision != account.Account.Revision)
            throw new DbUpdateConcurrencyException("The observed accounting scope changed. Read it again before preparing a review.");
        var now = clock.GetUtcNow().UtcDateTime;
        ReceptionistBudgetOpening? opening = null;
        if (input.Operation == "open_budget_period")
        {
            if (account.CurrentPeriod is not null || account.ExistingCeilings is null || input.Enabled is not null ||
                input.KnownCashCommitted is null || input.KnownConsumptionCommitted is null || input.KnownVoiceSeconds is null ||
                input.ReconciledThroughUtc is not { Kind: DateTimeKind.Utc } cutoff ||
                cutoff < account.CurrentMonthStartsUtc || cutoff >= account.CurrentMonthEndsUtc || cutoff > input.EvidenceObservedUtc)
                throw new ArgumentException("An absent current opening, existing accepted ceilings and explicit dated cash, consumption and voice facts are required.");
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(account.Account.TimeZoneId));
            opening = new(account.Account.Kind, account.Account.ScopeKey, account.Account.Currency, account.Account.TimeZoneId,
                local.Year, local.Month, account.ExistingCeilings.CashLimit, account.ExistingCeilings.ConsumptionLimit,
                input.KnownCashCommitted.Value, input.KnownConsumptionCommitted.Value, input.KnownVoiceSeconds.Value,
                input.EvidenceReference, cutoff);
        }
        else if (input.Enabled is null || input.KnownCashCommitted is not null || input.KnownConsumptionCommitted is not null ||
            input.KnownVoiceSeconds is not null || input.ReconciledThroughUtc is not null)
            throw new ArgumentException("Choose only the requested accounting permission; no opening or amount can accompany it.");
        if (input.EvidenceObservedUtc < account.CurrentMonthStartsUtc)
            throw new ArgumentException("Current-period accounting evidence is required.");
        var package = new ReceptionistOperatorPackage
        {
            Version = 1, Label = input.Operation == "open_budget_period" ? "Record current accounting opening with existing ceilings" : "Change existing accounting permission",
            Operation = input.Operation, ExpiresUtc = now.AddMinutes(30), MarketingConnectionId = observed.MarketingConnectionId,
            ExpectedOwnerKey = observed.OwnerKey, Provider = observed.Provider,
            ProviderProjectKey = observed.ProviderProjectKey ?? throw new InvalidOperationException("The current provider project is required."),
            ConfigurationId = observed.ConfigurationId, ConfigurationSha256 = observed.ConfigurationSha256,
            ExpectedConnectionRevision = observed.ConnectionRevision,
            ExpectedAccountRevisions = new() { [account.Account.Id] = account.Account.Revision },
            FundingScopes = account.Account.Kind == "funding" ? new() { [account.Account.Id] = account.Account.ScopeKey } : [],
            Opening = opening, BudgetAccountId = opening is null ? account.Account.Id : null,
            Enabled = input.Enabled, ReceiptReference = input.EvidenceReference,
            Evidence = [new(input.Operation, input.EvidenceReference, input.EvidenceSha256, input.EvidenceObservedUtc)]
        };
        var checkedPackage = package with { ReviewedByFounderOid = actor, ReviewedUtc = now };
        checkedPackage.Validate(actor, now);
        var json = JsonSerializer.Serialize(package, ReceptionistConfigurationCodec.Json);
        var hash = ReceptionistConfigurationCodec.Hash(json);
        await using var db = new MasterAppDbContext(DatabaseOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var state = await admission.ReadOperatorStateAsync(db, checkedPackage, hash, ct);
        if (state.CompletedReference is not null) throw new InvalidOperationException("The requested current accounting state is already recorded.");
        state.RequirePackageRevisions(checkedPackage);
        admission.ValidateBudgetAdmissionPreview(checkedPackage, state);
        var row = new ReceptionistOperationPackage
        {
            MarketingConnectionId = package.MarketingConnectionId, ConfigurationVersionId = package.ConfigurationId,
            Operation = package.Operation, PayloadJson = json, PayloadSha256 = hash,
            CreatedByActorId = actor, CreatedUtc = now, ExpiresUtc = package.ExpiresUtc
        };
        db.Add(row); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(row.Id, hash);
    }

    public async Task<IReadOnlyList<ReceptionistSavedOperationSummary>> SavedAccountingOperationsAsync(CancellationToken ct = default)
    {
        _ = RequireFounder();
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var rows = await db.Set<ReceptionistOperationPackage>().AsNoTracking().Where(x =>
            x.Operation == "open_budget_period" || x.Operation == "set_budget_admission")
            .OrderByDescending(x => x.CreatedUtc).Take(100).ToArrayAsync(ct);
        return rows.Select(x => new ReceptionistSavedOperationSummary(StoredPrefix + x.Id.ToString("N"), x.PayloadSha256,
            x.Operation, x.RevokedUtc is null ? x.ReviewState : "revoked", x.ExpiresUtc, x.Revision)).ToArray();
    }

    private async Task<MarketingOwnerScope> AccountingOperationOwnerAsync(Guid id, CancellationToken ct)
    {
        _ = RequireFounder();
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var row = await db.Set<ReceptionistOperationPackage>().AsNoTracking().SingleAsync(x => x.Id == id &&
            (x.Operation == "open_budget_period" || x.Operation == "set_budget_admission"), ct);
        var connection = await db.MarketingConnections.AsNoTracking().SingleAsync(x => x.Id == row.MarketingConnectionId, ct);
        return ReceptionistSql.Owner(connection);
    }

    public async Task<ReceptionistOperationReview> PreviewSavedAccountingAsync(Guid id, string hash, CancellationToken ct = default)
    {
        var owner = await AccountingOperationOwnerAsync(id, ct);
        return await PreviewPendingForOwnerAsync(owner, id, hash, (_, _) => Task.FromResult(true), ct);
    }

    public async Task ReviewSavedAccountingAsync(Guid id, Guid revision, string hash, bool accepted, CancellationToken ct = default)
    {
        var owner = await AccountingOperationOwnerAsync(id, ct);
        await ReviewForOwnerAsync(owner, id, revision, hash, accepted, (_, _) => Task.FromResult(true), ct);
    }
}
