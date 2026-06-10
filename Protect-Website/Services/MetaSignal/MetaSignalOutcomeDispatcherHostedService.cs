using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProtectWebsite.Services.Meta;
using Shared.Analytics;

namespace ProtectWebsite.Services.MetaSignal;

public sealed class MetaSignalOutcomeDispatcherHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> DispatchableEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppointmentCompleted",
        "ApplicationSubmitted",
        "PolicyIssued",
        "PolicyPaid"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MetaSignalIntelligenceOptions> _options;
    private readonly ILogger<MetaSignalOutcomeDispatcherHostedService> _logger;

    public MetaSignalOutcomeDispatcherHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MetaSignalIntelligenceOptions> options,
        ILogger<MetaSignalOutcomeDispatcherHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MetaSignal outcome dispatcher failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled || !_options.Value.SendServerEvents)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var capi = scope.ServiceProvider.GetRequiredService<IMetaConversionsApiService>();

        var rows = await db.MetaSignalEvents
            .Where(x =>
                !x.MetaServerSent &&
                x.LeadId != null &&
                x.TrafficType == "crm" &&
                DispatchableEvents.Contains(x.EventName))
            .OrderBy(x => x.CreatedUtc)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (!MetaSignalEventCatalog.TryGet(row.EventName, out var definition) || !definition.AllowServerForward)
                continue;

            var result = await capi.SendEventAsync(
                new MetaConversionsApiEventRequest
                {
                    LeadId = row.LeadId,
                    CorrelationId = Guid.NewGuid(),
                    EventName = row.EventName,
                    EventId = string.IsNullOrWhiteSpace(row.MetaDeduplicationKey) ? row.EventId : row.MetaDeduplicationKey,
                    QuoteType = row.QuoteType ?? "crm",
                    PageKey = row.EffectivePageKey ?? row.PageKey ?? "crm",
                    OfferKey = row.QuoteType ?? "crm",
                    EventSourceUrl = null,
                    AllowHashedContactData = false,
                    EventUtc = row.CreatedUtc == default ? DateTime.UtcNow : row.CreatedUtc,
                    CustomData = BuildCustomData(row)
                },
                cancellationToken);

            row.MetaServerSent = result.Sent;
            row.MetadataJson = MergeDispatchMetadata(row.MetadataJson, result);

            _logger.LogInformation(
                "MetaSignal outcome dispatcher event={EventName} rowId={RowId} leadId={LeadId} status={Status} sent={Sent}",
                row.EventName,
                row.Id,
                row.LeadId,
                result.Status,
                result.Sent);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object?> BuildCustomData(MetaSignalEvent row)
        => new()
        {
            ["event_category"] = row.EventCategory,
            ["quote_type"] = row.QuoteType,
            ["traffic_type"] = row.TrafficType,
            ["funnel_step"] = row.FunnelStep,
            ["step_name"] = row.StepName,
            ["score_tier"] = row.ScoreTier,
            ["total_signal_score"] = row.TotalSignalScore,
            ["source"] = "crm_outcome_dispatcher"
        };

    private static string MergeDispatchMetadata(string? existingJson, MetaConversionsApiResult result)
    {
        Dictionary<string, object?> metadata;

        try
        {
            metadata = string.IsNullOrWhiteSpace(existingJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson) ?? new Dictionary<string, object?>();
        }
        catch
        {
            metadata = new Dictionary<string, object?>
            {
                ["original_metadata_json"] = existingJson
            };
        }

        metadata["metaServerStatus"] = result.Status;
        metadata["metaServerNote"] = result.Note;
        metadata["metaServerAttempted"] = result.Attempted;
        metadata["metaServerSent"] = result.Sent;
        metadata["metaServerDispatchedUtc"] = DateTime.UtcNow;

        return JsonSerializer.Serialize(metadata);
    }
}
