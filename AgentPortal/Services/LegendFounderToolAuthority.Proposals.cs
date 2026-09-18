using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Auth;

namespace AgentPortal.Services;

internal sealed partial class LegendFounderToolAuthority
{
    private const string CloudRepairTool = "legend_prepare_software_repair";

    // Staging is durable review data only. It never calls the GitHub authority.
    internal async Task<FounderAiActionProposalReceipt> StageCloudRepairProposalAsync(
        ClaimsPrincipal founder, FounderAiActionScope sourceScope, FounderSoftwareRepairProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceScope = sourceScope with { Roles = sourceScope.Roles?.ToArray() ?? [] };
        if (proposal is null || FounderSoftwareRemediationService.ValidateProposal(proposal) is not null)
            return new(false, "cloud_proposal_invalid");
        var json = JsonSerializer.Serialize(new
        {
            base_sha = proposal.BaseSha, title = proposal.Title, summary = proposal.Summary,
            changes = proposal.Changes.Select(change => new { path = change.Path, content = change.Content })
        }, JsonOptions);
        if (!TryPrepareCloudAction(sourceScope, CloudRepairTool, json, out var arguments, out _) ||
            !TryReadCloudRepairArguments(arguments, out _) ||
            founder.GetCanonicalTenantId() != sourceScope.TenantId ||
            !await ReauthorizeCloudScopeAsync(founder, sourceScope, cancellationToken))
            return new(false, "cloud_proposal_scope_denied");
        using var services = _authorizationScopes!.CreateScope();
        if (!TryReadCloudReviewPolicy(services.ServiceProvider, out var policy) ||
            policy.AccountId != sourceScope.AccountId || policy.Environment != sourceScope.Environment)
            return new(false, "cloud_proposal_policy_unavailable");
        var now = DateTime.UtcNow;
        var row = new FounderAiActionAuthorization
        {
            ScopeDigest = ComputeCloudScopeDigest(sourceScope), AccountId = sourceScope.AccountId,
            TenantId = sourceScope.TenantId, UserId = sourceScope.UserId, SessionId = sourceScope.SessionId,
            ConversationId = Guid.Parse(sourceScope.ConversationId), RequestId = Guid.Parse(sourceScope.RequestId),
            Environment = sourceScope.Environment, AuthorizationVersion = sourceScope.AuthorizationVersion,
            ToolName = CloudRepairTool, AuthorizationKind = "FounderProposal", CanonicalArgumentsJson = arguments,
            CreatedUtc = now, ApprovedUtc = null, State = "Proposed",
            // Stable per source lease, never renewed by a repeated stage request.
            // Source admission is <=120s, so this remains at most 24h from now.
            ExpiresUtc = sourceScope.ExpiresUtc.AddHours(24).AddSeconds(-120),
            ReviewBindingJson = BuildCloudReviewBinding(policy, arguments)
        };
        row.ActionDigest = ComputeCloudProposalDigest(row);
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var existing = await db.FounderAiActionAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ActionDigest == row.ActionDigest, cancellationToken);
        if (existing is not null)
            return IsIntactCloudProposal(existing) && existing.State == "Proposed" && existing.ExpiresUtc > now
                ? new(true, Review: ProjectCloudProposal(existing)) : new(false, "cloud_proposal_unavailable");
        db.FounderAiActionAuthorizations.Add(row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return new(false, "cloud_proposal_conflict"); }
        return new(true, Review: ProjectCloudProposal(row));
    }

    internal async Task<FounderAiActionProposalReview?> GetCloudActionProposalAsync(
        ClaimsPrincipal founder, Guid proposalId, CancellationToken cancellationToken)
    {
        if (_authorizationScopes is null || founder.Identity?.IsAuthenticated != true || proposalId == Guid.Empty)
            return null;
        using var services = _authorizationScopes.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var row = await db.FounderAiActionAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == proposalId, cancellationToken);
        if (row is null || !IsIntactCloudProposal(row) || row.ExpiresUtc <= DateTime.UtcNow ||
            !TryReadCloudReviewPolicy(services.ServiceProvider, out var policy) ||
            row.AccountId != policy.AccountId || row.Environment != policy.Environment ||
            founder.GetCanonicalTenantId() != row.TenantId || string.IsNullOrWhiteSpace(founder.FindFirst("sid")?.Value))
            return null;
        try
        {
            var userId = await _legend.ResolveFounderActorAsync(founder, cancellationToken);
            if (userId != row.UserId) return null;
            // Canonical messaging owns conversation access; an expired source
            // operation is not revived merely to render the review screen.
            var history = await services.ServiceProvider.GetRequiredService<IMessagingService>()
                .GetFounderAiConversationPageAsync(new(userId, MessagingParticipantTypes.Agent), row.ConversationId,
                    new(Take: 1, IncludeGroupImage: false), cancellationToken);
            if (!history.Succeeded || history.Conversation is not { IsClosed: false }) return null;
            return ProjectCloudProposal(row);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    // The browser submits only identity/revision/digest. Tool, arguments and
    // workflow binding are loaded from the reviewed immutable proposal.
    internal async Task<FounderAiActionApprovalReceipt> ApproveCloudActionProposalAsync(
        ClaimsPrincipal founder, FounderAiActionScope newScope, Guid proposalId, string expectedRevision,
        string reviewDigest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        newScope = newScope with { Roles = newScope.Roles?.ToArray() ?? [] };
        if (proposalId == Guid.Empty || string.IsNullOrWhiteSpace(expectedRevision) || string.IsNullOrWhiteSpace(reviewDigest) ||
            founder.GetCanonicalTenantId() != newScope.TenantId ||
            !await ReauthorizeCloudScopeAsync(founder, newScope, cancellationToken))
            return new(false, "cloud_proposal_scope_denied");
        using var services = _authorizationScopes!.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var row = await db.FounderAiActionAuthorizations.SingleOrDefaultAsync(value => value.Id == proposalId, cancellationToken);
        if (row is null || !IsIntactCloudProposal(row) || row.State != "Proposed" || row.ApprovedUtc is not null ||
            row.Revision != expectedRevision || row.ActionDigest != reviewDigest || row.ExpiresUtc <= DateTime.UtcNow ||
            row.AccountId != newScope.AccountId || row.TenantId != newScope.TenantId || row.UserId != newScope.UserId ||
            row.Environment != newScope.Environment || row.ConversationId != Guid.Parse(newScope.ConversationId) ||
            row.RequestId == Guid.Parse(newScope.RequestId))
            return new(false, "cloud_proposal_review_changed");
        if (!TryReadCloudReviewPolicy(services.ServiceProvider, out var policy) || policy.AccountId != newScope.AccountId ||
            policy.Environment != newScope.Environment || BuildCloudReviewBinding(policy, row.CanonicalArgumentsJson) != row.ReviewBindingJson)
            return new(false, "cloud_proposal_policy_changed");
        if (!TryPrepareCloudAction(newScope, row.ToolName, row.CanonicalArgumentsJson, out var arguments, out var digest))
            return new(false, "cloud_proposal_invalid");
        var approval = CreateCloudActionRecord(newScope, row.ToolName, arguments, digest, newScope.ExpiresUtc, readOnly: false);
        approval.ParentProposalId = row.Id;
        approval.ReviewBindingJson = row.ReviewBindingJson;
        row.State = "Consumed";
        row.ApprovedUtc = DateTime.UtcNow;
        row.Revision = Guid.NewGuid().ToString("N");
        db.FounderAiActionAuthorizations.Add(approval);
        try
        {
            // One SaveChanges transaction + proposal Revision CAS + unique
            // ParentProposalId: at most one new approval can consume this review.
            await db.SaveChangesAsync(cancellationToken);
            return new(true, ApprovalId: approval.Id, ActionDigest: approval.ActionDigest, ExpiresUtc: approval.ExpiresUtc);
        }
        catch (DbUpdateException) { return new(false, "cloud_proposal_approval_conflict"); }
    }

    private async Task<bool> HasReviewedCloudRepairApprovalAsync(
        FounderAiActionAuthorization approval, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (approval.ToolName != CloudRepairTool || approval.ParentProposalId is not { } parentId ||
            !TryReadCloudReviewPolicy(services, out var policy)) return false;
        var proposal = await services.GetRequiredService<MasterAppDbContext>().FounderAiActionAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == parentId, cancellationToken);
        return proposal is not null && IsIntactCloudProposal(proposal) && proposal.State == "Consumed" &&
            proposal.ApprovedUtc is not null && proposal.ApprovedUtc <= proposal.ExpiresUtc &&
            proposal.AccountId == approval.AccountId && proposal.TenantId == approval.TenantId &&
            proposal.UserId == approval.UserId && proposal.ConversationId == approval.ConversationId &&
            proposal.RequestId != approval.RequestId && proposal.Environment == approval.Environment &&
            proposal.CanonicalArgumentsJson == approval.CanonicalArgumentsJson &&
            proposal.ReviewBindingJson == approval.ReviewBindingJson && policy.AccountId == approval.AccountId &&
            policy.Environment == approval.Environment && BuildCloudReviewBinding(policy, approval.CanonicalArgumentsJson) == approval.ReviewBindingJson;
    }

    private static bool TryReadCloudRepairArguments(string arguments, out FounderSoftwareRepairProposal? proposal)
    {
        proposal = null;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (!TryResolveFounderFunctionParameters(CloudRepairTool, out var schema) ||
                !IsStrictSchemaInstance(schema, root, allowRepairSourceText: true)) return false;
            proposal = new(root.GetProperty("base_sha").GetString()!, root.GetProperty("title").GetString()!,
                root.GetProperty("summary").GetString()!, root.GetProperty("changes").EnumerateArray()
                    .Select(change => new FounderSoftwareRepairChange(change.GetProperty("path").GetString()!, change.GetProperty("content").GetString()!)).ToArray());
            return FounderSoftwareRemediationService.ValidateProposal(proposal) is null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return false; }
    }

    private static bool IsIntactCloudProposal(FounderAiActionAuthorization row)
    {
        if (row.AuthorizationKind != "FounderProposal" || row.ToolName != CloudRepairTool || row.ParentProposalId is not null ||
            row.State is not ("Proposed" or "Consumed") || row.CreatedUtc is null || row.ReviewBindingJson is null ||
            !TryReadCloudRepairArguments(row.CanonicalArgumentsJson, out _)) return false;
        try { return row.ActionDigest == ComputeCloudProposalDigest(row); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static string ComputeCloudProposalDigest(FounderAiActionAuthorization row)
    {
        using var arguments = JsonDocument.Parse(row.CanonicalArgumentsJson);
        using var binding = JsonDocument.Parse(row.ReviewBindingJson!);
        return CloudHash(CanonicalCloudJson(JsonSerializer.SerializeToElement(new
        {
            version = "legend-founder-repair-proposal.v1", row.AccountId, row.TenantId, row.UserId, row.SessionId,
            row.ConversationId, sourceOperationId = row.RequestId, row.ScopeDigest, row.Environment,
            row.AuthorizationVersion, row.ToolName, arguments = arguments.RootElement, binding = binding.RootElement,
            reviewExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
        }, JsonOptions)));
    }

    private static string BuildCloudReviewBinding(CloudRepairReviewPolicy policy, string canonicalArguments)
    {
        using var arguments = JsonDocument.Parse(canonicalArguments);
        return CanonicalCloudJson(JsonSerializer.SerializeToElement(new
        {
            version = "legend-candidate-review.v1", repository = policy.Repository,
            baseSha = arguments.RootElement.GetProperty("base_sha").GetString(),
            // Full-file replacement digest, intentionally distinct from the
            // later candidate's actual git diff --binary patchSha256.
            changeSetSha256 = CloudHash(CanonicalCloudJson(arguments.RootElement.GetProperty("changes"))),
            trustedWorkflowSha = policy.TrustedWorkflowSha, profile = policy.Profile
        }, JsonOptions));
    }

    private static bool TryReadCloudReviewPolicy(IServiceProvider services, out CloudRepairReviewPolicy policy)
    {
        policy = null!;
        var config = services.GetService<IConfiguration>();
        if (!bool.TryParse(config?["FounderSoftwareRemediation:CandidateValidation:Enabled"], out var enabled) || !enabled)
            return false;
        var owner = config?["FounderSoftwareRemediation:RepositoryOwner"]?.Trim();
        var repository = config?["FounderSoftwareRemediation:RepositoryName"]?.Trim();
        var workflow = config?["FounderSoftwareRemediation:CandidateValidation:TrustedWorkflowSha"]?.Trim();
        var profile = config?["FounderSoftwareRemediation:CandidateValidation:Profile"];
        var account = config?["LegendConnect:Foundation:Cloudflare:AccountId"];
        var environment = config?["LegendConnect:Foundation:Cloudflare:Environment"];
        if (owner is null || repository is null || !Regex.IsMatch(owner, "\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\\z") ||
            !Regex.IsMatch(repository, "\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\\z") ||
            workflow is null || !Regex.IsMatch(workflow, "\\A[a-fA-F0-9]{40}\\z") || profile != "cloudflare-contracts" ||
            !ValidCloudIdentifier(account) || !ValidCloudIdentifier(environment)) return false;
        policy = new(owner + "/" + repository, workflow.ToLowerInvariant(), profile, account!, environment!);
        return true;
    }

    private bool CloudToolFeatureEnabled(string key)
    {
        if (_authorizationScopes is null) return false;
        using var services = _authorizationScopes.CreateScope();
        return bool.TryParse(services.ServiceProvider.GetService<IConfiguration>()?[key], out var enabled) && enabled;
    }

    private static FounderAiActionProposalReview ProjectCloudProposal(FounderAiActionAuthorization row) => new(
        row.Id, row.Revision, row.ActionDigest, row.ConversationId.ToString("D"), row.RequestId.ToString("D"),
        row.ToolName, row.CanonicalArgumentsJson, row.ReviewBindingJson!,
        DateTime.SpecifyKind(row.CreatedUtc!.Value, DateTimeKind.Utc), DateTime.SpecifyKind(row.ExpiresUtc, DateTimeKind.Utc), row.State);

    private sealed record CloudRepairReviewPolicy(string Repository, string TrustedWorkflowSha, string Profile, string AccountId, string Environment);
}

internal sealed record FounderAiActionProposalReceipt(
    bool Succeeded, string? Error = null, FounderAiActionProposalReview? Review = null);

internal sealed record FounderAiActionProposalReview(
    Guid ProposalId, string Revision, string ReviewDigest, string ConversationId, string SourceOperationId,
    string ToolName, string CanonicalArgumentsJson, string ReviewBindingJson, DateTime CreatedUtc, DateTime ReviewExpiresUtc, string State);
