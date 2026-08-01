using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace AgentPortal.Services;

public sealed class AzureAgentDirectorySyncHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AzureAgentDirectorySyncHostedService> _logger;

    public AzureAgentDirectorySyncHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AzureAgentDirectorySyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure agent directory sync failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task SyncOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var graph = scope.ServiceProvider.GetRequiredService<GraphServiceClient>();

        var activeAzureUsers = await LoadActiveAzureUsersAsync(graph, ct);
        if (activeAzureUsers.Count == 0)
        {
            _logger.LogWarning("Azure agent directory sync skipped because Graph returned zero active users.");
            return;
        }

        var profiles = await db.AgentProfiles.ToListAsync(ct);
        var resolvedProfiles = profiles
            .Select(profile => new ResolvedAgentProfile(
                profile,
                activeAzureUsers.ResolveObjectId(profile)))
            .ToArray();
        var canonicalProfileIds = resolvedProfiles
            .Where(resolved => resolved.AzureObjectId is not null)
            .GroupBy(resolved => resolved.AzureObjectId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectCanonicalProfile(group).Profile.Id)
            .ToHashSet();
        var changed = 0;

        foreach (var resolved in resolvedProfiles)
        {
            var profile = resolved.Profile;
            var existsInAzure = resolved.AzureObjectId is not null;

            if (existsInAzure && canonicalProfileIds.Contains(profile.Id))
            {
                if (!profile.IsActive)
                {
                    profile.IsActive = true;
                    profile.DeactivatedUtc = null;
                    profile.DeactivationReason = null;
                    profile.UpdatedUtc = DateTime.UtcNow;
                    changed++;
                }

                continue;
            }

            if (existsInAzure)
            {
                // An Entra user can have only one active AgentProfile. Older rows can
                // retain a development or pre-migration OID but match the same email;
                // leaving each one active makes the mobile directory show the person
                // multiple times. Keep the profile keyed by the active Entra OID when
                // present and preserve every alias as inactive historical data.
                if (profile.IsActive || profile.DeactivationReason != DuplicateProfileReason)
                {
                    profile.IsActive = false;
                    profile.DeactivatedUtc = DateTime.UtcNow;
                    profile.DeactivationReason = DuplicateProfileReason;
                    profile.UpdatedUtc = DateTime.UtcNow;
                    changed++;
                }

                continue;
            }

            var alreadyClean =
                !profile.IsActive &&
                profile.BookingEnabled == false &&
                string.IsNullOrWhiteSpace(profile.CalendarEmail) &&
                string.IsNullOrWhiteSpace(profile.CalendarUserId) &&
                string.IsNullOrWhiteSpace(profile.MicrosoftBookingsEmbedUrl) &&
                string.IsNullOrWhiteSpace(profile.FallbackBookingUrl) &&
                string.IsNullOrWhiteSpace(profile.BookingPageIdOrMailbox);

            if (alreadyClean) continue;

            profile.IsActive = false;
            profile.BookingEnabled = false;
            profile.CalendarEmail = null;
            profile.CalendarUserId = null;
            profile.MicrosoftBookingsEmbedUrl = null;
            profile.FallbackBookingUrl = null;
            profile.BookingPageIdOrMailbox = null;
            profile.DeactivatedUtc = DateTime.UtcNow;
            profile.DeactivationReason = "Agent no longer exists as an active Azure user.";
            profile.UpdatedUtc = DateTime.UtcNow;
            changed++;

            await db.GraphCalendarSubscriptions
                .Where(x =>
                    x.AgentUserId == profile.AgentUserId ||
                    (!string.IsNullOrWhiteSpace(profile.CalendarEmail) && x.CalendarEmail == profile.CalendarEmail))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.IsActive, false)
                    .SetProperty(x => x.LastError, "Agent no longer exists as an active Azure user.")
                    .SetProperty(x => x.UpdatedUtc, DateTime.UtcNow),
                    ct);
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Azure agent directory sync completed. Profiles={ProfileCount} Changed={ChangedCount} ActiveAzureUsers={AzureUserCount}",
            profiles.Count,
            changed,
            activeAzureUsers.Count);
    }

    private static async Task<AzureUserSet> LoadActiveAzureUsersAsync(GraphServiceClient graph, CancellationToken ct)
    {
        var objectIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emailObjectIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var response = await graph.Users.GetAsync(config =>
        {
            config.QueryParameters.Select = new[] { "id", "userPrincipalName", "mail", "accountEnabled" };
            config.QueryParameters.Top = 999;
        }, ct);

        foreach (var user in response?.Value ?? Enumerable.Empty<User>())
        {
            if (user.AccountEnabled == false) continue;

            var objectId = NormalizeKey(user.Id);
            if (objectId is null) continue;

            objectIds[objectId] = objectId;
            AddEmailObjectId(emailObjectIds, NormalizeEmail(user.UserPrincipalName), objectId);
            AddEmailObjectId(emailObjectIds, NormalizeEmail(user.Mail), objectId);
        }

        return new AzureUserSet(objectIds, emailObjectIds);
    }

    private static void AddEmailObjectId(
        IDictionary<string, string> emailObjectIds,
        string? email,
        string objectId)
    {
        if (!string.IsNullOrWhiteSpace(email)) emailObjectIds[email] = objectId;
    }

    private static ResolvedAgentProfile SelectCanonicalProfile(
        IEnumerable<ResolvedAgentProfile> profiles) =>
        profiles
            .OrderByDescending(profile => string.Equals(
                NormalizeKey(profile.Profile.AgentUserId),
                profile.AzureObjectId,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(profile => ProfileCompleteness(profile.Profile))
            .ThenBy(profile => profile.Profile.CreatedUtc)
            .ThenBy(profile => profile.Profile.Id)
            .First();

    private static int ProfileCompleteness(AgentProfile profile)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(profile.NormalizedEmail)) score += 2;
        if (!string.IsNullOrWhiteSpace(profile.FullName)) score++;
        if (!string.IsNullOrWhiteSpace(profile.Title)) score++;
        if (!string.IsNullOrWhiteSpace(profile.Phone)) score++;
        if (!string.IsNullOrWhiteSpace(profile.ShortBio)) score++;
        if (!string.IsNullOrWhiteSpace(profile.Npn)) score++;
        if (profile.ProfileImageContent is not null) score += 2;
        return score;
    }

    private static string? NormalizeEmail(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeKey(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record ResolvedAgentProfile(AgentProfile Profile, string? AzureObjectId);

    private sealed record AzureUserSet(
        IReadOnlyDictionary<string, string> ObjectIds,
        IReadOnlyDictionary<string, string> EmailObjectIds)
    {
        public int Count => ObjectIds.Count;

        public string? ResolveObjectId(AgentProfile profile)
        {
            var objectId = NormalizeKey(profile.AgentUserId);
            if (objectId is not null && ObjectIds.TryGetValue(objectId, out var resolvedByObjectId))
                return resolvedByObjectId;

            var matches = new[]
            {
                NormalizeEmail(profile.AgentUpn),
                NormalizeEmail(profile.NormalizedEmail),
                NormalizeEmail(profile.CalendarEmail)
            }
            .Where(email => email is not null)
            .Select(email => EmailObjectIds.GetValueOrDefault(email!))
            .Where(resolved => resolved is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

            return matches.Length == 1 ? matches[0] : null;
        }
    }

    private const string DuplicateProfileReason =
        "Superseded duplicate profile for the same active Entra user.";
}
