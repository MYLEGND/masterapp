using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Reception;

public sealed partial class ReceptionistOperatorService
{
    public async Task<ReceptionistSetupGuideProjection> SetupGuideForOwnerAsync(MarketingOwnerScope owner,
        Func<MarketingOwnerScope, CancellationToken, Task<bool>> authorize, CancellationToken ct = default)
    {
        await RequireOwnerActorAsync(owner, authorize, ct);
        // Business/agent routes never receive Founder instructions, even for a Founder viewing a business.
        return await ReadSetupGuideAsync(includeFounder: false, ct);
    }

    public async Task<ReceptionistSetupGuideProjection> SetupGuideForFounderAsync(CancellationToken ct = default)
    {
        _ = RequireFounder();
        return await ReadSetupGuideAsync(includeFounder: true, ct);
    }

    private async Task<ReceptionistSetupGuideProjection> ReadSetupGuideAsync(bool includeFounder, CancellationToken ct)
    {
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var row = await db.Set<ReceptionistSetupGuideRevision>().AsNoTracking()
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The published receptionist setup guide is unavailable.");
        return ProjectSetupGuide(row, includeFounder);
    }

    public async Task<ReceptionistSetupGuideProjection> PublishSetupGuideAsync(ReceptionistSetupGuidePublishInput input,
        CancellationToken ct = default)
    {
        var actor = RequireFounder();
        ArgumentNullException.ThrowIfNull(input);
        var business = ValidateSetupGuide(input.Business);
        var founder = ValidateSetupGuide(input.Founder);
        var businessJson = JsonSerializer.Serialize(business, ReceptionistConfigurationCodec.Json);
        var founderJson = JsonSerializer.Serialize(founder, ReceptionistConfigurationCodec.Json);
        // JSON escaping can make valid Unicode/plain-text fields larger than their character counts.
        // Check the actual stored representation before writing a revision the reader could reject.
        if (businessJson.Length > 131072 || founderJson.Length > 131072)
            throw new ArgumentException("The encoded guide is too large. Shorten its instructions before publishing.");
        if (input.ExpectedVersion < 1 || input.ExpectedRevision == Guid.Empty)
            throw new ArgumentException("The original published guide revision is required.");
        await using var db = new MasterAppDbContext(DatabaseOptions);
        var current = await db.Set<ReceptionistSetupGuideRevision>().AsNoTracking()
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The published receptionist setup guide is unavailable.");
        if (current.Version != input.ExpectedVersion || current.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The published instructions changed while you were editing.");
        var next = new ReceptionistSetupGuideRevision
        {
            Version = checked(current.Version + 1), Revision = Guid.NewGuid(),
            BusinessGuideJson = businessJson,
            FounderGuideJson = founderJson,
            CreatedByActorId = actor, CreatedUtc = clock.GetUtcNow().UtcDateTime
        };
        db.Add(next);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error)
        {
            // A unique next version chooses one publisher. Only a proven newer revision is a conflict;
            // other storage failures remain failures and never become a successful publication.
            await using var verify = new MasterAppDbContext(DatabaseOptions);
            if (await verify.Set<ReceptionistSetupGuideRevision>().AsNoTracking().AnyAsync(x => x.Version >= next.Version, ct))
                throw new DbUpdateConcurrencyException("The published instructions changed while you were editing.", error);
            throw;
        }
        // Independent readback of the immutable published revision, not a cache or a tenant copy.
        await using var readback = new MasterAppDbContext(DatabaseOptions);
        var saved = await readback.Set<ReceptionistSetupGuideRevision>().AsNoTracking()
            .SingleAsync(x => x.Version == next.Version && x.Revision == next.Revision, ct);
        return ProjectSetupGuide(saved, includeFounder: true);
    }

    private static ReceptionistSetupGuideProjection ProjectSetupGuide(ReceptionistSetupGuideRevision row, bool includeFounder)
    {
        var business = ReadDocument(row.BusinessGuideJson);
        var founder = includeFounder ? ReadDocument(row.FounderGuideJson) : null;
        return new(row.Version, row.Revision, DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc), business, founder);
    }

    private static ReceptionistSetupGuideDocument ReadDocument(string json)
    {
        if (json.Length > 131072) throw new InvalidOperationException("The published guide exceeds its supported size.");
        return ValidateSetupGuide(JsonSerializer.Deserialize<ReceptionistSetupGuideDocument>(json, ReceptionistConfigurationCodec.Json)
            ?? throw new InvalidOperationException("The published guide is invalid."));
    }

    private static ReceptionistSetupGuideDocument ValidateSetupGuide(ReceptionistSetupGuideDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        static string Text(string? value, int maximum)
        {
            var normalized = value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximum ||
                normalized.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
                throw new ArgumentException("Guide text must be complete and within its field limits.");
            return normalized;
        }
        if (document.Steps is null || document.Steps.Count is < 1 or > 16 || document.Steps.Any(x => x is null))
            throw new ArgumentException("Each guide needs between one and sixteen complete steps.");
        return new(Text(document.Title, 120), Text(document.Introduction, 600),
            document.Steps.Select(step => new ReceptionistSetupGuideStep(Text(step.Title, 120), Text(step.Instructions, 4000))).ToArray());
    }
}
