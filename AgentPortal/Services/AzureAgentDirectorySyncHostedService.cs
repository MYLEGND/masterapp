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
        var changed = 0;

        foreach (var profile in profiles)
        {
            var oid = NormalizeKey(profile.AgentUserId);
            var upn = NormalizeEmail(profile.AgentUpn);
            var normalizedEmail = NormalizeEmail(profile.NormalizedEmail);
            var calendarEmail = NormalizeEmail(profile.CalendarEmail);

            var existsInAzure =
                (!string.IsNullOrWhiteSpace(oid) && activeAzureUsers.ObjectIds.Contains(oid)) ||
                (!string.IsNullOrWhiteSpace(upn) && activeAzureUsers.Emails.Contains(upn)) ||
                (!string.IsNullOrWhiteSpace(normalizedEmail) && activeAzureUsers.Emails.Contains(normalizedEmail)) ||
                (!string.IsNullOrWhiteSpace(calendarEmail) && activeAzureUsers.Emails.Contains(calendarEmail));

            if (existsInAzure)
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
        var objectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var response = await graph.Users.GetAsync(config =>
        {
            config.QueryParameters.Select = new[] { "id", "userPrincipalName", "mail", "accountEnabled" };
            config.QueryParameters.Top = 999;
        }, ct);

        foreach (var user in response?.Value ?? Enumerable.Empty<User>())
        {
            if (user.AccountEnabled == false) continue;

            AddIfPresent(objectIds, NormalizeKey(user.Id));
            AddIfPresent(emails, NormalizeEmail(user.UserPrincipalName));
            AddIfPresent(emails, NormalizeEmail(user.Mail));
        }

        return new AzureUserSet(objectIds, emails);
    }

    private static void AddIfPresent(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) set.Add(value);
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

    private sealed record AzureUserSet(HashSet<string> ObjectIds, HashSet<string> Emails)
    {
        public int Count => ObjectIds.Count;
    }
}
