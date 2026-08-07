using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.DailyScripture;

public interface IDailyScriptureManagementService
{
    Task<bool> CanManageAsync(MessagingActor actor, CancellationToken cancellationToken = default);

    Task<DailyScriptureManagementResult<DailyScriptureManagementSnapshot>> GetSnapshotAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<DailyScriptureManagementResult<DailyScriptureOverrideSummary>> CreateAsync(
        MessagingActor actor,
        DailyScriptureOverrideDraft draft,
        CancellationToken cancellationToken = default);

    Task<DailyScriptureManagementResult<DailyScriptureOverrideSummary>> UpdateAsync(
        MessagingActor actor,
        Guid id,
        DailyScriptureOverrideDraft draft,
        CancellationToken cancellationToken = default);

    Task<DailyScriptureManagementResult> RemoveAsync(
        MessagingActor actor,
        Guid id,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticated authoring surface for the one Daily Scripture resolver. It
/// uses the existing participant-typed controlled-resource grant authority;
/// only the established Founder manager may distribute that grant.
/// </summary>
public sealed class DailyScriptureManagementService : IDailyScriptureManagementService
{
    private const int MaximumReferenceLength = 240;
    private const int MaximumTranslationLength = 40;
    private const int MaximumPassageLength = 20_000;

    private readonly MasterAppDbContext _db;
    private readonly IDailyScriptureService _resolver;
    private readonly IControlledResourceAccessService _controlledResources;

    public DailyScriptureManagementService(
        MasterAppDbContext db,
        IDailyScriptureService resolver,
        IControlledResourceAccessService controlledResources)
    {
        _db = db;
        _resolver = resolver;
        _controlledResources = controlledResources;
    }

    public async Task<bool> CanManageAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        var access = await _controlledResources.GetAccessAsync(
            actor,
            ControlledResourceTypes.ScriptureManagement,
            cancellationToken);
        return access.State == ControlledResourceAccessStates.Granted;
    }

    public async Task<DailyScriptureManagementResult<DailyScriptureManagementSnapshot>> GetSnapshotAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!await CanManageAsync(actor, cancellationToken))
            return DailyScriptureManagementResult<DailyScriptureManagementSnapshot>.Forbidden();

        var businessDate = _resolver.GetBusinessDate(DateTime.UtcNow);
        var current = await _resolver.GetForDateAsync(businessDate, cancellationToken);
        var upcoming = await _db.DailyScriptureOverrides
            .AsNoTracking()
            .Where(entry => entry.IsActive && entry.DisplayDate >= businessDate)
            .OrderBy(entry => entry.DisplayDate)
            .Take(90)
            .Select(entry => new DailyScriptureOverrideSummary(
                entry.Id,
                entry.DisplayDate,
                entry.Reference,
                entry.Translation,
                entry.PassageText,
                entry.CreatedUtc,
                entry.UpdatedUtc))
            .ToListAsync(cancellationToken);

        return DailyScriptureManagementResult<DailyScriptureManagementSnapshot>.Success(
            new DailyScriptureManagementSnapshot(businessDate, current, upcoming));
    }

    public async Task<DailyScriptureManagementResult<DailyScriptureOverrideSummary>> CreateAsync(
        MessagingActor actor,
        DailyScriptureOverrideDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (!await CanManageAsync(actor, cancellationToken))
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Forbidden();

        var validation = Validate(draft);
        if (validation is not null)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Invalid(validation);

        var today = _resolver.GetBusinessDate(DateTime.UtcNow);
        if (draft.DisplayDate < today)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Invalid(
                "Choose today or a future Legend date.");

        var hasExisting = await _db.DailyScriptureOverrides.AnyAsync(
            entry => entry.IsActive && entry.DisplayDate == draft.DisplayDate,
            cancellationToken);
        if (hasExisting)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Conflict(
                "A Daily Scripture override is already active for that date. Edit it instead.");

        var nowUtc = DateTime.UtcNow;
        var entry = new DailyScriptureOverride
        {
            Id = Guid.NewGuid(),
            DisplayDate = draft.DisplayDate,
            Reference = draft.Reference.Trim(),
            Translation = draft.Translation.Trim(),
            PassageText = draft.PassageText,
            IsActive = true,
            CreatedByUserId = actor.UserId,
            CreatedByParticipantType = actor.ParticipantType,
            CreatedUtc = nowUtc,
            UpdatedByUserId = actor.UserId,
            UpdatedByParticipantType = actor.ParticipantType,
            UpdatedUtc = nowUtc
        };
        _db.DailyScriptureOverrides.Add(entry);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Conflict(
                "A Daily Scripture override is already active for that date. Edit it instead.");
        }

        return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Success(ToSummary(entry));
    }

    public async Task<DailyScriptureManagementResult<DailyScriptureOverrideSummary>> UpdateAsync(
        MessagingActor actor,
        Guid id,
        DailyScriptureOverrideDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (!await CanManageAsync(actor, cancellationToken))
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Forbidden();

        var validation = Validate(draft);
        if (validation is not null)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Invalid(validation);

        var today = _resolver.GetBusinessDate(DateTime.UtcNow);
        if (draft.DisplayDate < today)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Invalid(
                "Choose today or a future Legend date.");

        var entry = await _db.DailyScriptureOverrides.SingleOrDefaultAsync(
            candidate => candidate.Id == id && candidate.IsActive,
            cancellationToken);
        if (entry is null)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.NotFound();

        var conflicts = await _db.DailyScriptureOverrides.AnyAsync(
            candidate => candidate.IsActive &&
                         candidate.DisplayDate == draft.DisplayDate &&
                         candidate.Id != id,
            cancellationToken);
        if (conflicts)
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Conflict(
                "A Daily Scripture override is already active for that date.");

        entry.DisplayDate = draft.DisplayDate;
        entry.Reference = draft.Reference.Trim();
        entry.Translation = draft.Translation.Trim();
        entry.PassageText = draft.PassageText;
        entry.UpdatedByUserId = actor.UserId;
        entry.UpdatedByParticipantType = actor.ParticipantType;
        entry.UpdatedUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Conflict(
                "Legend could not save this override because its date changed. Refresh and try again.");
        }

        return DailyScriptureManagementResult<DailyScriptureOverrideSummary>.Success(ToSummary(entry));
    }

    public async Task<DailyScriptureManagementResult> RemoveAsync(
        MessagingActor actor,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!await CanManageAsync(actor, cancellationToken))
            return DailyScriptureManagementResult.Forbidden();

        var entry = await _db.DailyScriptureOverrides.SingleOrDefaultAsync(
            candidate => candidate.Id == id && candidate.IsActive,
            cancellationToken);
        if (entry is null)
            return DailyScriptureManagementResult.NotFound();

        entry.IsActive = false;
        entry.UpdatedByUserId = actor.UserId;
        entry.UpdatedByParticipantType = actor.ParticipantType;
        entry.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return DailyScriptureManagementResult.Success();
    }

    private static string? Validate(DailyScriptureOverrideDraft draft)
    {
        if (draft.DisplayDate == default)
            return "Choose a date for this Daily Scripture.";
        if (string.IsNullOrWhiteSpace(draft.Reference) || draft.Reference.Trim().Length > MaximumReferenceLength)
            return "Enter a scripture reference of 240 characters or fewer.";
        if (string.IsNullOrWhiteSpace(draft.Translation) || draft.Translation.Trim().Length > MaximumTranslationLength)
            return "Enter a translation of 40 characters or fewer.";
        if (string.IsNullOrWhiteSpace(draft.PassageText) || draft.PassageText.Length > MaximumPassageLength)
            return "Enter the passage exactly as it should appear, up to 20,000 characters.";
        return null;
    }

    private static DailyScriptureOverrideSummary ToSummary(DailyScriptureOverride entry) => new(
        entry.Id,
        entry.DisplayDate,
        entry.Reference,
        entry.Translation,
        entry.PassageText,
        entry.CreatedUtc,
        entry.UpdatedUtc);
}

public sealed record DailyScriptureOverrideDraft(
    DateOnly DisplayDate,
    string Reference,
    string Translation,
    string PassageText);

public sealed record DailyScriptureOverrideSummary(
    Guid Id,
    DateOnly DisplayDate,
    string Reference,
    string Translation,
    string PassageText,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record DailyScriptureManagementSnapshot(
    DateOnly BusinessDate,
    DailyScripture Current,
    IReadOnlyList<DailyScriptureOverrideSummary> Upcoming);

public sealed record DailyScriptureManagementResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static DailyScriptureManagementResult Success() => new(true, null, null);
    public static DailyScriptureManagementResult Forbidden() => new(false, "DAILY_SCRIPTURE_FORBIDDEN", "You do not have permission to manage Daily Scripture.");
    public static DailyScriptureManagementResult NotFound() => new(false, "DAILY_SCRIPTURE_NOT_FOUND", "That Daily Scripture override is no longer available.");
}

public sealed record DailyScriptureManagementResult<T>(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    T? Value)
{
    public static DailyScriptureManagementResult<T> Success(T value) => new(true, null, null, value);
    public static DailyScriptureManagementResult<T> Forbidden() => new(false, "DAILY_SCRIPTURE_FORBIDDEN", "You do not have permission to manage Daily Scripture.", default);
    public static DailyScriptureManagementResult<T> Invalid(string message) => new(false, "DAILY_SCRIPTURE_INVALID", message, default);
    public static DailyScriptureManagementResult<T> Conflict(string message) => new(false, "DAILY_SCRIPTURE_CONFLICT", message, default);
    public static DailyScriptureManagementResult<T> NotFound() => new(false, "DAILY_SCRIPTURE_NOT_FOUND", "That Daily Scripture override is no longer available.", default);
}
