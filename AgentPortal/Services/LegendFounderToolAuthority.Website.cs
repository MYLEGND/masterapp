using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Infrastructure.WebsiteEditing.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Auth;

namespace AgentPortal.Services;

internal sealed partial class LegendFounderToolAuthority
{
    internal static bool IsWebsiteTool(string name) => name is
        "legend_website_inspect" or "legend_website_propose_edit" or "legend_website_propose_publish";

    private sealed record WebsiteAction(Guid WebsiteId, long ExpectedRevision, string? OperationsJson);

    // The model selects a resource, never its owner or an authorization ticket.
    private static async Task<WebsiteEditorTicket?> ResolveAssistantWebsiteAsync(
        IServiceProvider services, ClaimsPrincipal principal, WebsiteContentState state, CancellationToken ct)
    {
        AgentPortal.Security.FounderGuard.EnsureFounderOrThrow(principal);
        var actor = principal.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(actor)) return null;
        var db = services.GetRequiredService<MasterAppDbContext>();
        var config = services.GetRequiredService<IConfiguration>();
        WebsiteEditorTicket? ticket = null;
        if (state.SiteKey == WebsiteEditorSiteKeys.Legend && state.OwnerKey == WebsiteEditorSiteKeys.GlobalOwnerKey)
            ticket = new(state.SiteKey, state.OwnerKey, null, true, DateTime.UtcNow.AddMinutes(5), ActorUserId: actor);
        else if (state.SiteKey == WebsiteEditorSiteKeys.Protect)
        {
            var profile = await db.AgentTrackingProfiles.AsNoTracking().SingleOrDefaultAsync(x =>
                x.AgentUserId == state.OwnerKey && x.Status == "active", ct);
            if (profile is not null && IdentityKey.Normalize(profile.AgentUserId) == IdentityKey.Normalize(actor))
                ticket = new(state.SiteKey, state.OwnerKey, profile.Slug, true, DateTime.UtcNow.AddMinutes(5), ActorUserId: actor);
        }
        else if (state.SiteKey == WebsiteEditorSiteKeys.Business && state.CommerceBusinessId is { } business)
        {
            var members = await db.CommerceBusinessMembers.AsNoTracking().Where(x =>
                x.CommerceBusinessId == business && x.Status == "Active" && x.CanManageStorefront)
                .Select(x => x.ClientProfileId).Distinct().Take(101).ToListAsync(ct);
            if (members.Count > 100) return null;
            foreach (var profile in members)
                if (profile is { } profileId && await WebsiteBusinessAccess.CanManageAsActorAsync(db, business, profileId, actor,
                    principal.FindFirstValue(ClaimTypes.Email), ct))
                {
                    ticket = new(state.SiteKey, state.OwnerKey, null, true, DateTime.UtcNow.AddMinutes(5),
                        business, actor, principal.FindFirstValue(ClaimTypes.Email), profile);
                    break;
                }
        }
        if (ticket is null) return null;
        var protector = services.GetRequiredService<WebsiteEditorTicketProtector>();
        return await WebsiteTicketAuthorization.ResolveAsync(db, protector, config, protector.Protect(ticket), ct);
    }

    private static WebsitePlatformController WebsiteController(IServiceProvider services, ClaimsPrincipal principal) =>
        new(services.GetRequiredService<MasterAppDbContext>(), services.GetRequiredService<WebsiteEditorTicketProtector>(),
            services.GetRequiredService<IConfiguration>())
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services, User = principal } } };

    private static string WebsiteResult(IActionResult result) => JsonSerializer.Serialize(new {
        ok = result is ObjectResult { StatusCode: >= 200 and < 300 },
        status = result is ObjectResult value ? value.StatusCode : result is StatusCodeResult status ? status.StatusCode : 500,
        result = (result as ObjectResult)?.Value
    }, JsonOptions);

    internal async Task<string> ExecuteWebsiteToolAsync(ClaimsPrincipal principal, FounderAiToolCall call,
        Guid conversationId, Guid operationId, CancellationToken ct)
    {
        AgentPortal.Security.FounderGuard.EnsureFounderOrThrow(principal);
        if (_authorizationScopes is null || !IsWebsiteTool(call.Name)) return CloudActionFailure("website_service_unavailable");
        using var services = _authorizationScopes.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        using var arguments = JsonDocument.Parse(call.Arguments);
        if (!TryResolveFounderFunctionParameters(call.Name, out var schema) || !IsStrictSchemaInstance(schema, arguments.RootElement))
            return CloudActionFailure("website_arguments_invalid");
        var root = arguments.RootElement;
        var websiteId = root.GetProperty("website_id").GetString();
        if (call.Name == "legend_website_inspect" && string.IsNullOrWhiteSpace(websiteId))
        {
            var states = await db.Set<WebsiteContentState>().AsNoTracking().OrderBy(x => x.Id).Take(101).ToListAsync(ct);
            if (states.Count > 100) return CloudActionFailure("website_selection_requires_narrower_scope");
            var allowed = new List<object>();
            foreach (var state in states)
                if (await ResolveAssistantWebsiteAsync(services.ServiceProvider, principal, state, ct) is not null)
                    allowed.Add(new { websiteId = state.Id, state.SiteKey, state.CommerceBusinessId, state.Revision });
            return SerializeUnbounded(new { ok = true, websites = allowed, authority = "WebsiteTicketAuthorization" });
        }
        if (!Guid.TryParse(websiteId, out var id)) return CloudActionFailure("website_identity_invalid");
        var selected = await db.Set<WebsiteContentState>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (selected is null) return CloudActionFailure("website_unavailable");
        var actor = await ResolveAssistantWebsiteAsync(services.ServiceProvider, principal, selected, ct);
        if (actor is null) return CloudActionFailure("website_unavailable");
        var ticket = services.ServiceProvider.GetRequiredService<WebsiteEditorTicketProtector>().Protect(actor);
        var controller = WebsiteController(services.ServiceProvider, principal);
        if (call.Name == "legend_website_inspect")
        {
            var node = root.GetProperty("node_id").GetString();
            return WebsiteResult(string.IsNullOrWhiteSpace(node)
                ? await controller.CreativeWorkspaceSummary(ticket, ct)
                : await controller.CreativeNode(ticket, node, ct));
        }
        if (conversationId == Guid.Empty || operationId == Guid.Empty) return CloudActionFailure("website_conversation_required");
        var user = await _legend.ResolveFounderActorAsync(principal, ct);
        var history = await services.ServiceProvider.GetRequiredService<IMessagingService>().GetFounderAiConversationPageAsync(
            new(user, MessagingParticipantTypes.Agent), conversationId, new(Take: 1, IncludeGroupImage: false), ct);
        if (!history.Succeeded || history.Conversation is not { IsClosed: false }) return CloudActionFailure("website_conversation_unavailable");
        var revision = root.GetProperty("expected_revision").GetInt64();
        if (selected.Revision != revision) return CloudActionFailure("website_revision_changed");
        string? operations = call.Name == "legend_website_propose_edit" ? root.GetProperty("operations_json").GetString() : null;
        object preview;
        if (operations is not null)
        {
            List<WebsiteMutationOperation>? mutations;
            try { mutations = JsonSerializer.Deserialize<List<WebsiteMutationOperation>>(operations, JsonOptions); }
            catch (JsonException) { return CloudActionFailure("website_mutations_invalid"); }
            var result = await controller.ReviewAssistantMutations(new(ticket, revision, mutations), true, ct);
            if (result is not OkObjectResult ok) return WebsiteResult(result);
            preview = ok.Value!;
            operations = JsonSerializer.Serialize(mutations, JsonOptions);
        }
        else
        {
            if (actor.SiteKey == WebsiteEditorSiteKeys.Business &&
                !await WebsiteBusinessAccess.CanPublishAsync(db, actor.CommerceBusinessId!.Value, actor.ActorClientProfileId!.Value, ct))
                return CloudActionFailure("website_publish_forbidden");
            preview = new { content = WebsiteContentStore.ProjectPublishedText(selected.DraftJson),
                publication = "Publish this exact saved draft after separate approval; canonical quality gates still apply." };
        }
        var binding = AuthenticatedRequestBinding.Resolve(principal, DateTime.UtcNow);
        var tenant = principal.GetCanonicalTenantId();
        if (binding is null || string.IsNullOrWhiteSpace(tenant)) return CloudActionFailure("website_session_required");
        var row = new FounderAiActionAuthorization {
            ToolName = call.Name, AuthorizationKind = "WebsiteProposal", State = "Proposed",
            AccountId = selected.OwnerKey, TenantId = tenant, UserId = user, SessionId = binding.Id,
            ConversationId = conversationId, RequestId = operationId, Environment = "website-content",
            AuthorizationVersion = "website-ticket.v1", CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddHours(1),
            ScopeDigest = CloudHash(selected.Id.ToString("D") + ":" + selected.OwnerKey),
            CanonicalArgumentsJson = JsonSerializer.Serialize(new WebsiteAction(id, revision, operations), JsonOptions),
            ReviewBindingJson = JsonSerializer.Serialize(new { selected.Id, selected.SiteKey, selected.CommerceBusinessId,
                selected.Revision, draftSha256 = CloudHash(selected.DraftJson), preview }, JsonOptions)
        };
        // Stable identity for repeated proposals within the same operation.
        row.ExpiresUtc = history.Conversation.Messages.Last().SentUtc.AddHours(1);
        row.ActionDigest = ComputeCloudProposalDigest(row);
        var existing = await db.FounderAiActionAuthorizations.AsNoTracking().SingleOrDefaultAsync(x => x.ActionDigest == row.ActionDigest, ct);
        if (existing is null) { db.FounderAiActionAuthorizations.Add(row); await db.SaveChangesAsync(ct); }
        else row = existing;
        return SerializeUnbounded(new { ok = true, proposed = true, saved = false, published = false,
            proposalId = row.Id, reviewUrl = "/founder/legend-ai/actions/" + row.Id,
            reviewLink = "[Review exact website action](/founder/legend-ai/actions/" + row.Id + ")", preview });
    }

    private static bool IsIntactWebsiteProposal(FounderAiActionAuthorization row) =>
        row.AuthorizationKind == "WebsiteProposal" && row.ToolName is "legend_website_propose_edit" or "legend_website_propose_publish" &&
        row.ReviewBindingJson is not null && row.ActionDigest == ComputeCloudProposalDigest(row);

    internal async Task<FounderAiActionProposalReview?> GetWebsiteProposalAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        if (_authorizationScopes is null) return null;
        AgentPortal.Security.FounderGuard.EnsureFounderOrThrow(principal);
        using var services = _authorizationScopes.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var row = await db.FounderAiActionAuthorizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null || !IsIntactWebsiteProposal(row) || row.UserId != await _legend.ResolveFounderActorAsync(principal, ct) ||
            row.TenantId != principal.GetCanonicalTenantId() || row.ExpiresUtc <= DateTime.UtcNow) return null;
        var history = await services.ServiceProvider.GetRequiredService<IMessagingService>().GetFounderAiConversationPageAsync(
            new(row.UserId, MessagingParticipantTypes.Agent), row.ConversationId, new(Take: 1, IncludeGroupImage: false), ct);
        if (!history.Succeeded || history.Conversation is not { IsClosed: false }) return null;
        var action = JsonSerializer.Deserialize<WebsiteAction>(row.CanonicalArgumentsJson, JsonOptions)!;
        var state = await db.Set<WebsiteContentState>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == action.WebsiteId, ct);
        if (state is null || await ResolveAssistantWebsiteAsync(services.ServiceProvider, principal, state, ct) is null) return null;
        return ProjectCloudProposal(row);
    }
    internal static async Task<IActionResult> PublishAssistantWebsiteAsync(IServiceProvider services,
        WebsitePlatformController controller, string ticket, long expectedRevision, Guid requestId, CancellationToken ct)
    {
        // The same configured endpoint used by Account.WebsiteSession owns
        // compiler/runtime publication. Never send an editor ticket to a model URL.
        var config = services.GetRequiredService<IConfiguration>();
        var configured = config["WebsiteContentApiBaseUrl"] ?? config["LandingRoutes:BaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/")
            return new BadRequestObjectResult(new { error = "website_publication_endpoint_not_configured" });
        var endpoint = new Uri(origin, "/api/website-content/manage/publish");
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("WebsiteStudioPublishing");
        using var response = await client.PostAsJsonAsync(endpoint,
            new WebsitePlatformController.PublishRequest(ticket, expectedRevision, requestId), ct);
        // HTTP success is not publication proof. Read the immutable request ID
        // through the existing authority before returning an executed receipt.
        var status = await controller.PublicationStatus(ticket, requestId, ct, expectedRevision);
        if (status is OkObjectResult observed && JsonSerializer.SerializeToElement(observed.Value, JsonOptions)
            .GetProperty("found").GetBoolean()) return status;
        throw new HttpRequestException("website_publication_outcome_unknown");
    }

    internal async Task<string> ApproveWebsiteProposalAsync(ClaimsPrincipal principal, Guid id,
        string expectedRevision, string reviewDigest, Guid operationId, CancellationToken ct)
    {
        if (_authorizationScopes is null || operationId == Guid.Empty ||
            AuthenticatedRequestBinding.Resolve(principal, DateTime.UtcNow) is null)
            return CloudActionFailure("website_session_required");
        if (await GetWebsiteProposalAsync(principal, id, ct) is null) return CloudActionFailure("website_proposal_unavailable");
        using var services = _authorizationScopes.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var row = await db.FounderAiActionAuthorizations.SingleAsync(x => x.Id == id, ct);
        if (row.ActionDigest != reviewDigest) return CloudActionFailure("website_review_changed");
        if (row.State == "Completed" && row.ResultJson is not null) return row.ResultJson;
        var action = JsonSerializer.Deserialize<WebsiteAction>(row.CanonicalArgumentsJson, JsonOptions)!;
        var state = await db.Set<WebsiteContentState>().SingleOrDefaultAsync(x => x.Id == action.WebsiteId, ct);
        if (row.State is "Executing" or "OutcomeUnknown")
        {
            var recoveryActor = state is null ? null : await ResolveAssistantWebsiteAsync(services.ServiceProvider, principal, state, ct);
            if (action.OperationsJson is null && recoveryActor is not null)
            {
                var recoveryTicket = services.ServiceProvider.GetRequiredService<WebsiteEditorTicketProtector>().Protect(recoveryActor);
                var status = await WebsiteController(services.ServiceProvider, principal).PublicationStatus(recoveryTicket, row.Id, ct, action.ExpectedRevision);
                if (status is OkObjectResult observed && JsonSerializer.SerializeToElement(observed.Value, JsonOptions)
                    .GetProperty("found").GetBoolean())
                {
                    var recovered = WebsiteResult(status);
                    return await RecordCloudActionOutcomeAsync(row.Id, row.Revision, row.IdempotencyKey!, recovered, verifiedWebsitePublication: true)
                        ? recovered : CloudActionFailure("website_action_outcome_unknown");
                }
            }
            // Draft changes and their terminal receipt commit in one SaveChanges.
            // A missing terminal is not permission to repeat a write.
            return CloudActionFailure("website_action_outcome_unknown");
        }
        if (row.State != "Proposed" || row.Revision != expectedRevision || row.ExpiresUtc <= DateTime.UtcNow)
            return CloudActionFailure("website_approval_consumed_or_changed");
        using var binding = JsonDocument.Parse(row.ReviewBindingJson!);
        if (state is null || state.Revision != action.ExpectedRevision ||
            CloudHash(state.DraftJson) != binding.RootElement.GetProperty("draftSha256").GetString())
            return CloudActionFailure("website_draft_changed_review_again");
        var actor = await ResolveAssistantWebsiteAsync(services.ServiceProvider, principal, state, ct);
        if (actor is null || state.OwnerKey != row.AccountId) return CloudActionFailure("website_scope_changed");
        var ticket = services.ServiceProvider.GetRequiredService<WebsiteEditorTicketProtector>().Protect(actor);
        row.State = "Executing";
        row.ApprovedUtc = row.ExecutionStartedUtc = DateTime.UtcNow;
        row.IdempotencyKey = operationId.ToString("D");
        row.Revision = Guid.NewGuid().ToString("N");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return CloudActionFailure("website_approval_conflict"); }
        var claimRevision = row.Revision;
        try
        {
            var controller = WebsiteController(services.ServiceProvider, principal);
            var result = action.OperationsJson is null
                ? await PublishAssistantWebsiteAsync(services.ServiceProvider, controller, ticket, action.ExpectedRevision, row.Id, ct)
                : await controller.ReviewAssistantMutations(new(ticket, action.ExpectedRevision,
                    JsonSerializer.Deserialize<List<WebsiteMutationOperation>>(action.OperationsJson, JsonOptions)), false, ct,
                    committedRevision => {
                        row.State = "Completed";
                        row.CompletedUtc = DateTime.UtcNow;
                        row.ResultJson = SerializeUnbounded(new { ok = true, status = 200,
                            result = new { saved = true, published = false, websiteId = action.WebsiteId, revision = committedRevision } });
                        row.Revision = Guid.NewGuid().ToString("N");
                    });
            if (action.OperationsJson is not null && result is OkObjectResult && row.ResultJson is not null)
                return row.ResultJson;
            var receipt = WebsiteResult(result);
            if (!await RecordCloudActionOutcomeAsync(row.Id, claimRevision, row.IdempotencyKey, receipt))
                return CloudActionFailure("website_action_outcome_unknown");
            return receipt;
        }
        catch (Exception)
        {
            await RecordCloudActionOutcomeAsync(row.Id, claimRevision, row.IdempotencyKey, null);
            return CloudActionFailure("website_action_outcome_unknown");
        }
    }

}
