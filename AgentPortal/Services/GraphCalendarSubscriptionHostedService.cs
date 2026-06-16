using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed class GraphCalendarSubscriptionHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<GraphCalendarSubscriptionHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public GraphCalendarSubscriptionHostedService(
        IServiceProvider services,
        ILogger<GraphCalendarSubscriptionHostedService> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _services = services;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

            var publicBaseUrl = FirstNotEmpty(
                _configuration["GraphWebhooks__PublicBaseUrl"],
                _configuration["GraphWebhooks:PublicBaseUrl"])?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(publicBaseUrl))
            {
                _logger.LogInformation("Graph calendar subscription sync skipped because GraphWebhooks:PublicBaseUrl is not configured.");
                return;
            }

            var accessToken = await TryGetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("Graph calendar subscription sync skipped because app-only Graph token could not be acquired.");
                return;
            }

            var agents = await db.AgentProfiles
                .AsNoTracking()
                .Where(x => x.BookingEnabled == true &&
                            (!string.IsNullOrWhiteSpace(x.CalendarUserId) ||
                             !string.IsNullOrWhiteSpace(x.CalendarEmail) ||
                             !string.IsNullOrWhiteSpace(x.BookingPageIdOrMailbox)))
                .ToListAsync(cancellationToken);

            foreach (var agent in agents)
            {
                foreach (var calendarIdentity in ResolveCalendarIdentities(agent))
                {
                    await EnsureSubscriptionForAgentAsync(db, accessToken, publicBaseUrl, agent, calendarIdentity, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph calendar subscription sync failed.");
        }
    }

    private async Task EnsureSubscriptionForAgentAsync(
        MasterAppDbContext db,
        string accessToken,
        string publicBaseUrl,
        AgentProfile agent,
        string calendarIdentity,
        CancellationToken cancellationToken)
    {
        calendarIdentity = calendarIdentity.Trim();
        if (string.IsNullOrWhiteSpace(calendarIdentity))
        {
            return;
        }

        var resource = $"users/{calendarIdentity}/events";

        var existing = await db.GraphCalendarSubscriptions
            .Where(x => x.AgentUserId == agent.AgentUserId &&
                        x.IsActive &&
                        (x.Resource == resource ||
                         x.CalendarUserId == calendarIdentity ||
                         x.CalendarEmail == calendarIdentity))
            .OrderByDescending(x => x.ExpirationUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var renewCutoff = DateTime.UtcNow.AddHours(12);
        if (existing != null && existing.ExpirationUtc > renewCutoff)
        {
            return;
        }

        if (existing != null && !string.IsNullOrWhiteSpace(existing.GraphSubscriptionId))
        {
            var renewed = await TryRenewSubscriptionAsync(accessToken, existing, cancellationToken);
            if (renewed)
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            existing.IsActive = false;
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        var created = await TryCreateSubscriptionAsync(accessToken, publicBaseUrl, agent, calendarIdentity, cancellationToken);
        if (!string.IsNullOrWhiteSpace(created.GraphSubscriptionId))
        {
            db.GraphCalendarSubscriptions.Add(created);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "Graph calendar subscription was not persisted because Graph did not return a subscription id. agent={AgentUserId} calendar={CalendarIdentity} error={Error}",
                agent.AgentUserId,
                calendarIdentity,
                created.LastError);
        }
    }

    private static IReadOnlyList<string> ResolveCalendarIdentities(AgentProfile agent)
    {
        return new[]
        {
            agent.CalendarUserId,
            agent.CalendarEmail,
            agent.BookingPageIdOrMailbox
        }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<bool> TryRenewSubscriptionAsync(
        string accessToken,
        GraphCalendarSubscription subscription,
        CancellationToken cancellationToken)
    {
        var newExpiration = DateTime.UtcNow.AddHours(70);
        var payload = JsonSerializer.Serialize(new
        {
            expirationDateTime = newExpiration.ToString("o")
        });

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientDefault");
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"https://graph.microsoft.com/v1.0/subscriptions/{Uri.EscapeDataString(subscription.GraphSubscriptionId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                subscription.LastError = $"Renew failed: {(int)response.StatusCode} {responseText}";
                subscription.UpdatedUtc = DateTime.UtcNow;
                return false;
            }

            var result = JsonSerializer.Deserialize<GraphSubscriptionResponse>(responseText, JsonOptions);
            subscription.ExpirationUtc = ParseGraphDateTime(result?.ExpirationDateTime) ?? newExpiration;
            subscription.LastRenewedUtc = DateTime.UtcNow;
            subscription.LastError = null;
            subscription.UpdatedUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            subscription.LastError = ex.Message;
            subscription.UpdatedUtc = DateTime.UtcNow;
            return false;
        }
    }

    private async Task<GraphCalendarSubscription> TryCreateSubscriptionAsync(
        string accessToken,
        string publicBaseUrl,
        AgentProfile agent,
        string calendarIdentity,
        CancellationToken cancellationToken)
    {
        var expiration = DateTime.UtcNow.AddHours(70);
        var clientState = Guid.NewGuid().ToString("N");
        var notificationUrl = $"{publicBaseUrl}/api/graph/calendar-webhook";
        var resource = $"users/{calendarIdentity}/events";

        var payload = JsonSerializer.Serialize(new
        {
            changeType = "created,updated,deleted",
            notificationUrl,
            resource,
            expirationDateTime = expiration.ToString("o"),
            clientState
        });

        var row = new GraphCalendarSubscription
        {
            Id = Guid.NewGuid(),
            AgentUserId = agent.AgentUserId,
            CalendarUserId = string.IsNullOrWhiteSpace(agent.CalendarUserId) ? null : agent.CalendarUserId.Trim(),
            CalendarEmail = calendarIdentity.Contains('@') ? calendarIdentity.Trim() : agent.CalendarEmail?.Trim(),
            Resource = resource,
            ChangeType = "created,updated,deleted",
            ClientState = clientState,
            ExpirationUtc = expiration,
            IsActive = false,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientDefault");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/subscriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                row.LastError = $"Create failed: {(int)response.StatusCode} {responseText}";
                return row;
            }

            var result = JsonSerializer.Deserialize<GraphSubscriptionResponse>(responseText, JsonOptions);
            row.GraphSubscriptionId = result?.Id ?? "";
            row.ExpirationUtc = ParseGraphDateTime(result?.ExpirationDateTime) ?? expiration;
            row.LastRenewedUtc = DateTime.UtcNow;
            row.IsActive = !string.IsNullOrWhiteSpace(row.GraphSubscriptionId);
            row.LastError = null;
            return row;
        }
        catch (Exception ex)
        {
            row.LastError = ex.Message;
            return row;
        }
    }

    private async Task<string?> TryGetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        try
        {
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }),
                cancellationToken);
            return token.Token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire Graph app token for subscription sync.");
            return null;
        }
    }

    private static string? FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static DateTime? ParseGraphDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var dto) ? dto.UtcDateTime : null;
    }

    private sealed class GraphSubscriptionResponse
    {
        public string? Id { get; set; }
        public string? ExpirationDateTime { get; set; }
    }
}
