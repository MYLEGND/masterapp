using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public interface IAdvertisingActionAuthorizationService
{
    Task<AdvertisingActionProposalSnapshot> ProposeAsync(
        MarketingOwnerScope owner,
        string proposalKind,
        AdvertisingMutationPlan plan,
        string proposedByUserId,
        JsonElement? sourceSnapshot = null,
        CancellationToken ct = default);

    Task<AdvertisingActionApprovalReceipt> ApproveAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string approvedByUserId,
        string expectedRevision,
        DateTime approvalExpiresUtc,
        CancellationToken ct = default);

    Task<AdvertisingActionExecutionReceipt> ExecuteAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string expectedRevision,
        CancellationToken ct = default);

    Task<AdvertisingActionProposalSnapshot> RejectAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string rejectedByUserId,
        string expectedRevision,
        CancellationToken ct = default);

    Task<AdvertisingActionProposalSnapshot?> GetAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AdvertisingActionProposalSnapshot>> ListAsync(
        MarketingOwnerScope owner,
        int limit = 100,
        CancellationToken ct = default);
}

public sealed class AdvertisingActionAuthorizationService(
    MasterAppDbContext db,
    IOpenAiAdsExecutionService openAiAds) : IAdvertisingActionAuthorizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AdvertisingActionProposalSnapshot> ProposeAsync(
        MarketingOwnerScope owner,
        string proposalKind,
        AdvertisingMutationPlan plan,
        string proposedByUserId,
        JsonElement? sourceSnapshot = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidatePlan(plan);
        var actor = Required(proposedByUserId, 450, nameof(proposedByUserId));
        var kind = Required(proposalKind, 40, nameof(proposalKind));
        var exactPlanJson = CanonicalJson(JsonSerializer.SerializeToElement(plan, JsonOptions));
        var digest = Hash(owner.Key + "\n" + MarketingDestinationKeys.OpenAi + "\n" + exactPlanJson);

        var existing = await db.AdvertisingActionAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.OwnerKey == owner.Key &&
                x.Provider == MarketingDestinationKeys.OpenAi &&
                x.ActionDigest == digest, ct);
        if (existing is not null)
            return Snapshot(owner, existing);

        var row = new AdvertisingActionAuthorization
        {
            OwnerKey = owner.Key,
            OwnerType = owner.OwnerType,
            AgentTrackingProfileId = owner.AgentTrackingProfileId,
            CommerceBusinessId = owner.CommerceBusinessId,
            Provider = MarketingDestinationKeys.OpenAi,
            ProposalKind = kind,
            ActionDigest = digest,
            ExactPlanJson = exactPlanJson,
            SourceSnapshotJson = sourceSnapshot.HasValue ? CanonicalJson(sourceSnapshot.Value) : null,
            State = AdvertisingActionStates.Proposed,
            ProposedByUserId = actor,
            ProposedUtc = DateTime.UtcNow,
            Revision = NewRevision()
        };
        db.AdvertisingActionAuthorizations.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await db.AdvertisingActionAuthorizations.AsNoTracking()
                .SingleAsync(x =>
                    x.OwnerKey == owner.Key &&
                    x.Provider == MarketingDestinationKeys.OpenAi &&
                    x.ActionDigest == digest, ct);
            return Snapshot(owner, winner);
        }

        return Snapshot(owner, row);
    }

    public async Task<AdvertisingActionApprovalReceipt> ApproveAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string approvedByUserId,
        string expectedRevision,
        DateTime approvalExpiresUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actor = Required(approvedByUserId, 450, nameof(approvedByUserId));
        if (approvalExpiresUtc.Kind != DateTimeKind.Utc ||
            approvalExpiresUtc <= DateTime.UtcNow ||
            approvalExpiresUtc > DateTime.UtcNow.AddMinutes(30))
            throw new ArgumentException("Advertising approval expiry must be UTC and no more than 30 minutes in the future.", nameof(approvalExpiresUtc));

        var row = await LoadOwnedAsync(owner, proposalId, ct);
        RequireRevision(row, expectedRevision);
        if (row.State != AdvertisingActionStates.Proposed)
            throw new InvalidOperationException("Only a proposed advertising action can be approved.");

        ValidatePlan(ReadPlan(row.ExactPlanJson));
        row.State = AdvertisingActionStates.Approved;
        row.ApprovedByUserId = actor;
        row.ApprovedUtc = DateTime.UtcNow;
        row.ApprovalExpiresUtc = approvalExpiresUtc;
        row.Revision = NewRevision();
        await db.SaveChangesAsync(ct);

        return new(row.Id, row.ActionDigest, row.State, row.Revision, approvalExpiresUtc);
    }

    public async Task<AdvertisingActionExecutionReceipt> ExecuteAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string expectedRevision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var row = await LoadOwnedAsync(owner, proposalId, ct);
        RequireRevision(row, expectedRevision);

        if (row.State == AdvertisingActionStates.Completed && row.ProviderReceiptJson is not null)
            return ExecutionReceipt(row);

        if (row.State != AdvertisingActionStates.Approved ||
            row.ApprovalExpiresUtc is null ||
            row.ApprovalExpiresUtc <= DateTime.UtcNow ||
            string.IsNullOrWhiteSpace(row.ApprovedByUserId))
            throw new InvalidOperationException("A current exact advertising approval is required before execution.");

        var plan = ReadPlan(row.ExactPlanJson);
        ValidatePlan(plan);
        if (Hash(owner.Key + "\n" + MarketingDestinationKeys.OpenAi + "\n" + CanonicalJson(JsonSerializer.SerializeToElement(plan, JsonOptions))) != row.ActionDigest)
            throw new InvalidOperationException("Advertising action digest no longer matches the reviewed plan.");

        var claim = Guid.NewGuid().ToString("N");
        row.State = AdvertisingActionStates.Executing;
        row.ExecutionClaimToken = claim;
        row.ExecutionStartedUtc = DateTime.UtcNow;
        row.Revision = NewRevision();
        var claimRevision = row.Revision;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("Advertising approval was consumed by another execution.");
        }

        var receipts = new List<object>();
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            foreach (var step in plan.Steps)
            {
                var result = await ExecuteStepAsync(owner, step, createdIds, ct);
                var providerId = step.ActionType == AdvertisingActionTypes.CreativeUploadUrl
                    ? ReadString(result, "file_id")
                    : ReadString(result, "id");
                if (!string.IsNullOrWhiteSpace(providerId))
                    createdIds[step.StepKey] = providerId!;
                receipts.Add(new
                {
                    step.StepKey,
                    step.ActionType,
                    providerId,
                    provider = result
                });
            }

            await CompleteAsync(
                row.Id,
                claim,
                claimRevision,
                AdvertisingActionStates.Completed,
                JsonSerializer.Serialize(receipts, JsonOptions),
                null,
                null,
                ct);

            return await ReadExecutionReceiptAsync(owner, row.Id, ct);
        }
        catch (OpenAiAdsExecutionException ex)
        {
            var state = ex.Retryable ? AdvertisingActionStates.OutcomeUnknown : AdvertisingActionStates.Failed;
            await CompleteAsync(
                row.Id,
                claim,
                claimRevision,
                state,
                JsonSerializer.Serialize(receipts, JsonOptions),
                "openai_ads_http_" + ex.HttpStatusCode,
                Clamp(ex.Message),
                CancellationToken.None);
            return await ReadExecutionReceiptAsync(owner, row.Id, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            await CompleteAsync(
                row.Id,
                claim,
                claimRevision,
                AdvertisingActionStates.Failed,
                JsonSerializer.Serialize(receipts, JsonOptions),
                "advertising_execution_invalid",
                Clamp(ex.Message),
                CancellationToken.None);
            return await ReadExecutionReceiptAsync(owner, row.Id, ct);
        }
    }

    public async Task<AdvertisingActionProposalSnapshot> RejectAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        string rejectedByUserId,
        string expectedRevision,
        CancellationToken ct = default)
    {
        var actor = Required(rejectedByUserId, 450, nameof(rejectedByUserId));
        var row = await LoadOwnedAsync(owner, proposalId, ct);
        RequireRevision(row, expectedRevision);
        if (row.State is not (AdvertisingActionStates.Proposed or AdvertisingActionStates.Approved))
            throw new InvalidOperationException("Only an unexecuted advertising action can be rejected.");
        row.State = AdvertisingActionStates.Rejected;
        row.ApprovedByUserId = null;
        row.ApprovedUtc = null;
        row.ApprovalExpiresUtc = null;
        row.RejectedByUserId = actor;
        row.RejectedUtc = DateTime.UtcNow;
        row.Revision = NewRevision();
        await db.SaveChangesAsync(ct);
        return Snapshot(owner, row);
    }

    public async Task<AdvertisingActionProposalSnapshot?> GetAsync(
        MarketingOwnerScope owner,
        Guid proposalId,
        CancellationToken ct = default)
    {
        var row = await db.AdvertisingActionAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proposalId && x.OwnerKey == owner.Key, ct);
        return row is null ? null : Snapshot(owner, row);
    }

    public async Task<IReadOnlyList<AdvertisingActionProposalSnapshot>> ListAsync(
        MarketingOwnerScope owner,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (limit is < 1 or > 250) throw new ArgumentOutOfRangeException(nameof(limit));

        var rows = await db.AdvertisingActionAuthorizations.AsNoTracking()
            .Where(x => x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.OpenAi)
            .OrderByDescending(x => x.ProposedUtc)
            .ThenByDescending(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(row => Snapshot(owner, row)).ToArray();
    }

    private async Task<JsonElement> ExecuteStepAsync(
        MarketingOwnerScope owner,
        AdvertisingMutationPlanStep step,
        IReadOnlyDictionary<string, string> createdIds,
        CancellationToken ct)
    {
        string? ParentId()
        {
            if (string.IsNullOrWhiteSpace(step.ParentStepKey)) return null;
            return createdIds.TryGetValue(step.ParentStepKey, out var value)
                ? value
                : throw new InvalidOperationException($"Approved parent step '{step.ParentStepKey}' did not produce an ID.");
        }

        return step.ActionType switch
        {
            AdvertisingActionTypes.CampaignCreate =>
                (await openAiAds.CreateCampaignAsync(owner, Deserialize<OpenAiAdsCampaignCreateRequest>(step.Payload), ct)).Payload,

            AdvertisingActionTypes.CampaignUpdate =>
                (await openAiAds.UpdateCampaignAsync(owner, Deserialize<OpenAiAdsCampaignUpdateRequest>(step.Payload), ct)).Payload,

            AdvertisingActionTypes.CampaignStatus =>
                await ExecuteStatusAsync(owner, "campaign", step.Payload, ct),

            AdvertisingActionTypes.AdGroupCreate =>
                (await openAiAds.CreateAdGroupAsync(owner,
                    Deserialize<OpenAiAdsAdGroupCreateRequest>(step.Payload) with
                    {
                        CampaignId = ParentId() ?? Deserialize<OpenAiAdsAdGroupCreateRequest>(step.Payload).CampaignId
                    }, ct)).Payload,

            AdvertisingActionTypes.AdGroupUpdate =>
                (await openAiAds.UpdateAdGroupAsync(owner, Deserialize<OpenAiAdsAdGroupUpdateRequest>(step.Payload), ct)).Payload,

            AdvertisingActionTypes.AdGroupStatus =>
                await ExecuteStatusAsync(owner, "ad_group", step.Payload, ct),

            AdvertisingActionTypes.AdCreate =>
                (await openAiAds.CreateAdAsync(owner,
                    BindAdCreateDependencies(
                        Deserialize<OpenAiAdsAdCreateRequest>(step.Payload),
                        ParentId(),
                        CreativeId(step.CreativeStepKey, createdIds)), ct)).Payload,

            AdvertisingActionTypes.AdUpdate =>
                (await openAiAds.UpdateAdAsync(owner, Deserialize<OpenAiAdsAdUpdateRequest>(step.Payload), ct)).Payload,

            AdvertisingActionTypes.AdStatus =>
                await ExecuteStatusAsync(owner, "ad", step.Payload, ct),

            AdvertisingActionTypes.ConversionSettingCreate =>
                (await openAiAds.CreateConversionEventSettingAsync(
                    owner,
                    Deserialize<OpenAiAdsConversionEventSettingCreateRequest>(step.Payload),
                    ct)).Payload,

            AdvertisingActionTypes.CreativeUploadUrl =>
                UploadReceipt(await openAiAds.UploadImageUrlAsync(
                    owner,
                    Deserialize<AdvertisingCreativeUploadUrlMutation>(step.Payload).ImageUrl,
                    ct)),

            _ => throw new InvalidOperationException($"Unsupported advertising action '{step.ActionType}'.")
        };
    }

    private async Task<JsonElement> ExecuteStatusAsync(
        MarketingOwnerScope owner,
        string kind,
        JsonElement payload,
        CancellationToken ct)
    {
        var request = Deserialize<AdvertisingStatusMutation>(payload);
        return kind switch
        {
            "campaign" => (await openAiAds.SetCampaignStatusAsync(owner, request.EntityId, request.Status, ct)).Payload,
            "ad_group" => (await openAiAds.SetAdGroupStatusAsync(owner, request.EntityId, request.Status, ct)).Payload,
            "ad" => (await openAiAds.SetAdStatusAsync(owner, request.EntityId, request.Status, ct)).Payload,
            _ => throw new InvalidOperationException("Unsupported advertising status mutation.")
        };
    }

    private async Task CompleteAsync(
        Guid id,
        string claim,
        string claimRevision,
        string state,
        string receiptJson,
        string? errorCode,
        string? errorMessage,
        CancellationToken ct)
    {
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recovery.CancelAfter(TimeSpan.FromSeconds(5));
        var row = await db.AdvertisingActionAuthorizations
            .SingleOrDefaultAsync(x => x.Id == id, recovery.Token);
        if (row is null ||
            row.State != AdvertisingActionStates.Executing ||
            row.ExecutionClaimToken != claim ||
            row.Revision != claimRevision)
            return;

        row.State = state;
        row.ProviderReceiptJson = receiptJson;
        row.ErrorCode = errorCode;
        row.ErrorMessage = errorMessage;
        row.CompletedUtc = DateTime.UtcNow;
        row.Revision = NewRevision();
        await db.SaveChangesAsync(recovery.Token);
    }

    private async Task<AdvertisingActionExecutionReceipt> ReadExecutionReceiptAsync(
        MarketingOwnerScope owner,
        Guid id,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var row = await LoadOwnedAsync(owner, id, ct);
        return ExecutionReceipt(row);
    }

    private async Task<AdvertisingActionAuthorization> LoadOwnedAsync(
        MarketingOwnerScope owner,
        Guid id,
        CancellationToken ct)
    {
        var row = await db.AdvertisingActionAuthorizations.SingleOrDefaultAsync(
            x => x.Id == id && x.OwnerKey == owner.Key && x.Provider == MarketingDestinationKeys.OpenAi, ct);
        return row ?? throw new InvalidOperationException("Advertising proposal does not exist in this scope.");
    }

    private static AdvertisingActionExecutionReceipt ExecutionReceipt(AdvertisingActionAuthorization row) =>
        new(
            row.Id,
            row.State,
            row.Revision,
            ParseOptional(row.ProviderReceiptJson),
            row.ErrorCode,
            row.ErrorMessage);

    private static AdvertisingActionProposalSnapshot Snapshot(
        MarketingOwnerScope owner,
        AdvertisingActionAuthorization row) =>
        new(
            row.Id,
            owner,
            row.Provider,
            row.ProposalKind,
            row.ActionDigest,
            ReadPlan(row.ExactPlanJson),
            row.State,
            row.ProposedByUserId,
            row.ProposedUtc,
            row.ApprovedByUserId,
            row.ApprovedUtc,
            row.ApprovalExpiresUtc,
            row.ExecutionStartedUtc,
            row.CompletedUtc,
            ParseOptional(row.SourceSnapshotJson),
            ParseOptional(row.ProviderReceiptJson),
            row.ErrorCode,
            row.ErrorMessage,
            row.Revision);

    private static void ValidatePlan(AdvertisingMutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.Version, "legend-ad-plan.v1", StringComparison.Ordinal))
            throw new ArgumentException("Unsupported advertising plan version.", nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.Title) || plan.Title.Length > 300)
            throw new ArgumentException("Advertising plan title is required.", nameof(plan));
        if (plan.Steps is null || plan.Steps.Count == 0 || plan.Steps.Count > 50)
            throw new ArgumentException("Advertising plan must contain 1–50 exact actions.", nameof(plan));

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepKey) || step.StepKey.Length > 80 ||
                step.StepKey.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
                throw new ArgumentException("Advertising step keys must be stable ASCII identifiers.", nameof(plan));
            if (!keys.Add(step.StepKey))
                throw new ArgumentException("Advertising step keys must be unique.", nameof(plan));
            if (!AdvertisingActionTypes.Supported.Contains(step.ActionType))
                throw new ArgumentException($"Unsupported advertising action '{step.ActionType}'.", nameof(plan));
            if (step.Payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Advertising step payload must be an object.", nameof(plan));
            if (!string.IsNullOrWhiteSpace(step.ParentStepKey) && !keys.Contains(step.ParentStepKey))
                throw new ArgumentException("Advertising parent steps must precede dependent actions.", nameof(plan));
            if (!string.IsNullOrWhiteSpace(step.CreativeStepKey) && !keys.Contains(step.CreativeStepKey))
                throw new ArgumentException("Advertising creative steps must precede dependent actions.", nameof(plan));

            ValidateTypedPayload(step);
        }
    }

    private static void ValidateTypedPayload(AdvertisingMutationPlanStep step)
    {
        switch (step.ActionType)
        {
            case AdvertisingActionTypes.CampaignCreate:
                RequireCreateIdempotency(Deserialize<OpenAiAdsCampaignCreateRequest>(step.Payload).IdempotencyKey);
                break;
            case AdvertisingActionTypes.AdGroupCreate:
                RequireCreateIdempotency(Deserialize<OpenAiAdsAdGroupCreateRequest>(step.Payload).IdempotencyKey);
                break;
            case AdvertisingActionTypes.AdCreate:
                RequireCreateIdempotency(Deserialize<OpenAiAdsAdCreateRequest>(step.Payload).IdempotencyKey);
                break;
            case AdvertisingActionTypes.ConversionSettingCreate:
                RequireCreateIdempotency(Deserialize<OpenAiAdsConversionEventSettingCreateRequest>(step.Payload).IdempotencyKey);
                break;
            case AdvertisingActionTypes.CreativeUploadUrl:
                _ = Deserialize<AdvertisingCreativeUploadUrlMutation>(step.Payload);
                break;
            case AdvertisingActionTypes.CampaignUpdate:
                _ = Deserialize<OpenAiAdsCampaignUpdateRequest>(step.Payload);
                break;
            case AdvertisingActionTypes.AdGroupUpdate:
                _ = Deserialize<OpenAiAdsAdGroupUpdateRequest>(step.Payload);
                break;
            case AdvertisingActionTypes.AdUpdate:
                _ = Deserialize<OpenAiAdsAdUpdateRequest>(step.Payload);
                break;
            case AdvertisingActionTypes.CampaignStatus:
            case AdvertisingActionTypes.AdGroupStatus:
            case AdvertisingActionTypes.AdStatus:
                _ = Deserialize<AdvertisingStatusMutation>(step.Payload);
                break;
        }
    }

    private static OpenAiAdsAdCreateRequest BindAdCreateDependencies(
        OpenAiAdsAdCreateRequest request,
        string? adGroupId,
        string? fileId)
    {
        var creative = request.Creative;
        if (!string.IsNullOrWhiteSpace(fileId))
            creative = creative with { FileId = fileId };
        return request with
        {
            AdGroupId = string.IsNullOrWhiteSpace(adGroupId) ? request.AdGroupId : adGroupId,
            Creative = creative
        };
    }

    private static string? CreativeId(
        string? stepKey,
        IReadOnlyDictionary<string, string> createdIds)
    {
        if (string.IsNullOrWhiteSpace(stepKey)) return null;
        return createdIds.TryGetValue(stepKey, out var value)
            ? value
            : throw new InvalidOperationException($"Approved creative step '{stepKey}' did not produce a file ID.");
    }

    private static JsonElement UploadReceipt(OpenAiAdsImageUploadResult result) =>
        JsonSerializer.SerializeToElement(new { file_id = result.FileId, provider = result.Payload }, JsonOptions);

    private static void RequireCreateIdempotency(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 255)
            throw new ArgumentException("Every advertising create action requires a stable idempotency key.");
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions)
        ?? throw new ArgumentException($"Invalid advertising payload for {typeof(T).Name}.");

    private static AdvertisingMutationPlan ReadPlan(string json) =>
        JsonSerializer.Deserialize<AdvertisingMutationPlan>(json, JsonOptions)
        ?? throw new InvalidOperationException("Stored advertising plan is invalid.");

    private static JsonElement? ParseOptional(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void RequireRevision(AdvertisingActionAuthorization row, string expectedRevision)
    {
        if (!string.Equals(row.Revision, expectedRevision, StringComparison.Ordinal))
            throw new DbUpdateConcurrencyException("Advertising proposal changed. Reload before continuing.");
    }

    private static string Required(string? value, int max, string parameter)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl))
            throw new ArgumentException("A valid value is required.", parameter);
        return text;
    }

    private static string NewRevision() => Guid.NewGuid().ToString("N");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Clamp(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Advertising execution failed." : value.Trim();
        return text.Length <= 4000 ? text : text[..4000];
    }

    private static string CanonicalJson(JsonElement value)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, value);
        return builder.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var properties = value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < properties.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(JsonSerializer.Serialize(properties[i].Name));
                    builder.Append(':');
                    AppendCanonical(builder, properties[i].Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) builder.Append(',');
                    first = false;
                    AppendCanonical(builder, item);
                }
                builder.Append(']');
                break;
            default:
                builder.Append(value.GetRawText());
                break;
        }
    }

    private sealed record AdvertisingStatusMutation(string EntityId, string Status);
    private sealed record AdvertisingCreativeUploadUrlMutation(string ImageUrl);
}
