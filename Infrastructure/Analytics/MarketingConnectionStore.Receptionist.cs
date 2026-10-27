using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Reception;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public sealed partial class MarketingConnectionStore
{
    /// <summary>The caller must resolve the actor through the existing website/workspace settings authorization.</summary>
    public async Task<ReceptionistConfigurationSnapshot> GetReceptionistConfigurationAsync(
        MarketingOwnerScope owner, string provider, CancellationToken ct = default, bool operating = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = MarketingDestinationKeys.Normalize(provider);
        await using var read = new MasterAppDbContext(ReceptionistSql.Options(db));
        var connection = await read.MarketingConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == key, ct);
        if (connection is null)
            return new(owner, key, Guid.Empty, null, 0, null, false, false, null, []);
        return await ReceptionistSnapshotAsync(read, owner, connection, ct, operating);
    }

    /// <summary>Edits create an immutable revision. They never grant provider acceptance, funding or Ads permission.</summary>
    public async Task<ReceptionistConfigurationSnapshot> SaveReceptionistConfigurationAsync(
        MarketingOwnerScope owner, string provider, Guid expectedConnectionRevision,
        ReceptionistBusinessConfiguration configuration, string authorizedActorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = MarketingDestinationKeys.Normalize(provider);
        var json = ReceptionistConfigurationCodec.Serialize(configuration);
        ReceptionistConfigurationCodec.Required(authorizedActorId, 450, nameof(authorizedActorId));
        var options = ReceptionistSql.Options(db);
        var accountIds = await ReceptionistOwnerAccountIdsAsync(options, owner, ct);
        return await ReceptionistSql.TransactionAsync(options, accountIds, async write =>
        {
            var connection = await ReceptionistConnectionForWriteAsync(write, owner, key, expectedConnectionRevision, ct);
            if (write.Entry(connection).State == EntityState.Added)
                await write.SaveChangesAsync(ct); // The configuration FK is created after its existing authority row.
            var previous = await write.Set<ReceptionistConfigurationVersion>()
                .Where(x => x.MarketingConnectionId == connection.Id).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            var version = new ReceptionistConfigurationVersion
            {
                MarketingConnectionId = connection.Id,
                Version = checked((previous?.Version ?? 0) + 1),
                ConfigurationJson = json,
                ConfigurationSha256 = ReceptionistConfigurationCodec.Hash(json),
                // Any owner edit is a new inactive draft. Old provider acceptance remains with its old immutable version.
                AcceptedBindingJson = null,
                CreatedByActorId = authorizedActorId,
                CreatedUtc = DateTime.UtcNow
            };
            write.Add(version);
            await write.SaveChangesAsync(ct);
            // A draft never changes the accepted operating selection or fences active calls.
            // Legacy unaccepted active selections remain unaccepted until explicit review.
            Touch(connection);
            await write.SaveChangesAsync(ct);
            return await ReceptionistSnapshotAsync(write, owner, connection, ct);
        }, ct);
    }

    public Task<ReceptionistConfigurationSnapshot> SetReceptionistAdmissionAsync(
        MarketingOwnerScope owner, string provider, Guid expectedConnectionRevision, bool enabled,
        CancellationToken ct = default) => SetReceptionistAdmissionCoreAsync(owner, provider,
            expectedConnectionRevision, enabled, null, ct);

    private async Task<ReceptionistConfigurationSnapshot> SetReceptionistAdmissionCoreAsync(
        MarketingOwnerScope owner, string provider, Guid expectedConnectionRevision, bool enabled,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var key = MarketingDestinationKeys.Normalize(provider);
        var options = ReceptionistSql.Options(db);
        var accountIds = await ReceptionistOwnerAccountIdsAsync(options, owner, ct);
        return await ReceptionistSql.TransactionAsync(options, accountIds, async write =>
        {
            if (guard is not null) await guard(write);
            var connection = await ReceptionistConnectionForWriteAsync(write, owner, key, expectedConnectionRevision, ct, mustExist: true);
            if (enabled)
            {
                var version = await write.Set<ReceptionistConfigurationVersion>().SingleOrDefaultAsync(
                    x => x.Id == connection.ReceptionistActiveConfigurationId && x.MarketingConnectionId == connection.Id, ct)
                    ?? throw new InvalidOperationException("Receptionist configuration is required.");
                var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
                var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration);
                if (binding is null || connection.ReceptionistProviderProjectKey != binding.ProviderProjectKey ||
                    !ReceptionistConfigurationCodec.HasRequiredIntake(configuration, binding))
                    throw new InvalidOperationException("Verified provider and canonical intake acceptance are required.");
            }
            if (connection.ReceptionistAdmissionEnabled != enabled)
                connection.ReceptionistControlRevision = Guid.NewGuid();
            connection.ReceptionistAdmissionEnabled = enabled;
            Touch(connection);
            await write.SaveChangesAsync(ct);
            return await ReceptionistSnapshotAsync(write, owner, connection, ct);
        }, ct);
    }

    /// <summary>Only the operator/provider acceptance path can bind verified routes and shared funding accounts.</summary>
    internal Task<ReceptionistConfigurationSnapshot> AcceptReceptionistBindingAsync(
        MarketingOwnerScope owner, string provider, Guid expectedConnectionRevision,
        ReceptionistProviderBinding binding, string authorizedActorId, CancellationToken ct = default) =>
        AcceptReceptionistBindingCoreAsync(owner, provider, expectedConnectionRevision, binding, authorizedActorId, null, ct);

    private async Task<ReceptionistConfigurationSnapshot> AcceptReceptionistBindingCoreAsync(
        MarketingOwnerScope owner, string provider, Guid expectedConnectionRevision,
        ReceptionistProviderBinding binding, string authorizedActorId,
        Func<MasterAppDbContext, Task>? guard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Costs is null || binding.Costs.Count is < 1 or > 32 || binding.Costs.Any(x => x is null || x.FundingBudgetAccountId == Guid.Empty))
            throw new ArgumentException("A verified bounded provider cost map is required.");
        var key = MarketingDestinationKeys.Normalize(provider);
        ReceptionistConfigurationCodec.Required(authorizedActorId, 450, nameof(authorizedActorId));
        var options = ReceptionistSql.Options(db);
        var accountIds = (await ReceptionistOwnerAccountIdsAsync(options, owner, ct))
            .Concat(binding.Costs.Select(x => x.FundingBudgetAccountId)).Distinct().ToArray();
        return await ReceptionistSql.TransactionAsync(options, accountIds, async write =>
        {
            if (guard is not null) await guard(write);
            var connection = await ReceptionistConnectionForWriteAsync(write, owner, key, expectedConnectionRevision, ct, mustExist: true);
            var previous = await write.Set<ReceptionistConfigurationVersion>()
                .Where(x => x.MarketingConnectionId == connection.Id).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Owner-authored configuration is required before provider acceptance.");
            var configuration = ReceptionistConfigurationCodec.Read(previous.ConfigurationJson, previous.ConfigurationSha256);
            ReceptionistConfigurationCodec.ValidateBinding(binding, configuration);
            if (binding.BudgetPolicy is { } policy && (guard is null || binding.OperatorAcceptancePackageSha256 is null ||
                policy.OwnerKey != owner.Key || policy.MarketingConnectionId != connection.Id ||
                policy.SourceConfigurationId != previous.Id || policy.ConfigurationSha256 != previous.ConfigurationSha256))
                throw new InvalidOperationException("Budget policy acceptance requires the exact guarded reviewed source configuration.");
            if (connection.ReceptionistProviderProjectKey is { } existingProject && existingProject != binding.ProviderProjectKey)
                throw new InvalidOperationException("An existing exclusive provider project cannot be changed through ordinary binding acceptance; a separately reviewed project-transfer lifecycle is required.");
            if (await write.MarketingConnections.AsNoTracking().AnyAsync(x => x.Id != connection.Id &&
                x.Provider == key && x.ReceptionistProviderProjectKey == binding.ProviderProjectKey, ct))
                throw new InvalidOperationException("This provider project is already exclusively claimed by another owner, including inactive configurations.");
            // These are existing trusted accounting dimensions; a business cannot create them through this DTO.
            var fundingIds = binding.Costs.Select(x => x.FundingBudgetAccountId).Distinct().ToArray();
            var funding = await write.Set<ReceptionistBudgetAccount>().AsNoTracking()
                .Where(x => fundingIds.Contains(x.Id)).ToListAsync(ct);
            if (funding.Count != fundingIds.Length || funding.Any(x => x.Kind != "funding" || x.Currency != configuration.Currency))
                throw new InvalidOperationException("Every provider component requires an existing matching-currency funding authority.");
            var version = new ReceptionistConfigurationVersion
            {
                Id = binding.BudgetPolicy?.AcceptedConfigurationId ?? Guid.NewGuid(),
                MarketingConnectionId = connection.Id,
                Version = checked(previous.Version + 1),
                ConfigurationJson = previous.ConfigurationJson,
                ConfigurationSha256 = previous.ConfigurationSha256,
                AcceptedBindingJson = JsonSerializer.Serialize(binding, ReceptionistConfigurationCodec.Json),
                CreatedByActorId = authorizedActorId,
                CreatedUtc = DateTime.UtcNow
            };
            write.Add(version);
            await write.SaveChangesAsync(ct);
            connection.ReceptionistActiveConfigurationId = version.Id;
            connection.ReceptionistIngressKey = binding.IngressKey;
            connection.ReceptionistProviderProjectKey ??= binding.ProviderProjectKey;
            connection.ReceptionistAdmissionEnabled = false;
            connection.ReceptionistControlRevision = Guid.NewGuid();
            Touch(connection);
            await write.SaveChangesAsync(ct);
            return await ReceptionistSnapshotAsync(write, owner, connection, ct);
        }, ct);
    }

    private static async Task<Guid[]> ReceptionistOwnerAccountIdsAsync(
        DbContextOptions<MasterAppDbContext> options, MarketingOwnerScope owner, CancellationToken ct)
    {
        await using var read = new MasterAppDbContext(options);
        return await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
            .Where(x => x.Kind == "owner" && x.ScopeKey == owner.Key).Select(x => x.Id).ToArrayAsync(ct);
    }

    private static async Task<MarketingConnection> ReceptionistConnectionForWriteAsync(
        MasterAppDbContext write, MarketingOwnerScope owner, string provider, Guid expectedRevision,
        CancellationToken ct, bool mustExist = false)
    {
        await write.MarketingConnections.Where(x => x.OwnerKey == owner.Key && x.Provider == provider)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Revision, x => x.Revision), ct);
        var connection = await write.MarketingConnections.SingleOrDefaultAsync(x => x.OwnerKey == owner.Key && x.Provider == provider, ct);
        if (connection is null)
        {
            if (mustExist || expectedRevision != Guid.Empty)
                throw new DbUpdateConcurrencyException("Receptionist settings changed or are absent. Reload before editing.");
            connection = New(owner, provider);
            write.Add(connection);
        }
        else if (connection.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("Receptionist settings changed. Reload before editing.");
        if (ReceptionistSql.Owner(connection) != owner)
            throw new InvalidOperationException("Receptionist owner identity is inconsistent.");
        return connection;
    }

    private static async Task<ReceptionistConfigurationSnapshot> ReceptionistSnapshotAsync(
        MasterAppDbContext read, MarketingOwnerScope owner, MarketingConnection connection, CancellationToken ct, bool operating = false)
    {
        if (ReceptionistSql.Owner(connection) != owner)
            throw new InvalidOperationException("Receptionist owner identity is inconsistent.");
        var versions = read.Set<ReceptionistConfigurationVersion>().AsNoTracking().Where(x => x.MarketingConnectionId == connection.Id);
        var version = operating
            ? await versions.SingleOrDefaultAsync(x => x.Id == connection.ReceptionistActiveConfigurationId, ct)
            : await versions.OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (version is null)
            return new(owner, connection.Provider, connection.Revision, null, 0, null, false, false, null, []);
        var configuration = ReceptionistConfigurationCodec.Read(version.ConfigurationJson, version.ConfigurationSha256);
        var binding = ReceptionistConfigurationCodec.ReadBinding(version.AcceptedBindingJson, configuration);
        var capabilities = configuration.RequestedCapabilities.Select(key =>
        {
            var accepted = binding?.Capabilities.SingleOrDefault(x => x.Key == key);
            return new ReceptionistCapabilityStatus(key, accepted?.State ?? ReceptionistCapabilityStates.Unverified, accepted?.AcceptanceReceipt);
        }).ToArray();
        var ownerAccount = await read.Set<ReceptionistBudgetAccount>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Kind == "owner" && x.ScopeKey == owner.Key, ct);
        ReceptionistAccountingStatus? accounting = null;
        if (ownerAccount is not null)
        {
            var now = DateTime.UtcNow;
            var period = await read.Set<ReceptionistBudgetPeriod>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.BudgetAccountId == ownerAccount.Id && x.StartUtc <= now && x.EndUtc > now, ct);
            if (period is not null)
            {
                var outstanding = await read.Set<ReceptionistCostReservation>().AsNoTracking()
                    .Where(x => x.OwnerBudgetAccountId == ownerAccount.Id && (!x.CashFinal || !x.ConsumptionFinal)).ToListAsync(ct);
                accounting = new(period.OpeningReconciled, ownerAccount.AdmissionEnabled, period.CashLimit, period.ConsumptionLimit,
                    configuration.MonthlyCashLimit <= period.CashLimit && configuration.MonthlyConsumptionLimit <= period.ConsumptionLimit,
                    period.CashCommitted, period.ConsumptionCommitted,
                    outstanding.Sum(x => x.CashFinal ? 0m : Math.Max(0m, x.CashReserved - x.CashObserved)),
                    outstanding.Sum(x => x.ConsumptionFinal ? 0m : Math.Max(0m, x.ConsumptionReserved - x.ConsumptionObserved)));
            }
        }
        return new(owner, connection.Provider, connection.Revision, version.Id, version.Version,
            version.ConfigurationSha256, connection.ReceptionistAdmissionEnabled,
            binding is not null && connection.ReceptionistProviderProjectKey == binding.ProviderProjectKey &&
                ReceptionistConfigurationCodec.HasRequiredIntake(configuration, binding), configuration, capabilities, accounting);
    }
}
