using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderCloudActionApprovalTests
{
    [Fact]
    public async Task WebsiteProposalsAreProviderNeutralAndNeverExposeBackendWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        var authority = fixture.NewAuthority();
        static string? Name(object tool) => JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString();
        foreach (var tools in new[] {
            authority.GetAvailableTools(false, fixture.Scope.ConversationId, LegendConnectExternalProviderPolicy.ProviderEnabled, true),
            authority.GetAvailableCloudTools(fixture.Scope.ConversationId, LegendConnectExternalProviderPolicy.CloudflareFoundation) })
        {
            Assert.Contains(tools, x => Name(x) == "legend_website_propose_edit");
            Assert.Contains(tools, x => Name(x) == "legend_website_propose_publish");
            Assert.DoesNotContain(tools, x => Name(x) == "legend_release_approved_repair");
        }
        Assert.False(authority.IsReadOnly("legend_website_propose_publish"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebsiteExactDraftApprovalPreservesPreviewAndRejectsChangedDraft(bool changeDraft)
    {
        await using var fixture = await Fixture.CreateAsync(true);
        ((System.Security.Claims.ClaimsIdentity)fixture.Principal.Identity!).AddClaim(new System.Security.Claims.Claim("tid", "tenant-fixture"));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Legend,
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey, Revision = 1,
            DraftJson = JsonSerializer.Serialize(new WebsiteContentDocument(), json) };
        fixture.Db.Add(state);
        await fixture.Db.SaveChangesAsync();
        var original = state.DraftJson;
        var call = new FounderAiToolCall("website-test", "legend_website_propose_edit", JsonSerializer.Serialize(new {
            website_id = state.Id.ToString("D"), expected_revision = 1,
            operations_json = JsonSerializer.Serialize(new[] { new WebsiteMutationOperation { Type = "insertNode", Scope = "shell.footer",
                Node = new WebsiteCompositionNode { Id = "review.copy", Type = "text", Tag = "p", Text = "Reviewed copy" } } }, json)
        }));
        var authority = fixture.NewAuthority();
        var proposed = await authority.ExecuteWebsiteToolAsync(fixture.Principal, call, Guid.Parse(fixture.Scope.ConversationId),
            Guid.Parse(fixture.Scope.RequestId), CancellationToken.None);
        using var parsed = JsonDocument.Parse(proposed);
        Assert.True(parsed.RootElement.TryGetProperty("ok", out var success) && success.GetBoolean(), proposed);
        Assert.Equal(original, (await fixture.Db.Set<WebsiteContentState>().AsNoTracking().SingleAsync()).DraftJson);
        var proposal = await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync(x => x.AuthorizationKind == "WebsiteProposal");
        if (changeDraft) { state.Revision++; await fixture.Db.SaveChangesAsync(); }
        var receipt = await authority.ApproveWebsiteProposalAsync(fixture.Principal, proposal.Id, proposal.Revision,
            proposal.ActionDigest, Guid.NewGuid(), CancellationToken.None);
        if (changeDraft) { Assert.Contains("website_draft_changed_review_again", receipt); return; }
        using var outcome = JsonDocument.Parse(receipt);
        Assert.True(outcome.RootElement.GetProperty("ok").GetBoolean(), receipt);
        var after = await fixture.Db.Set<WebsiteContentState>().AsNoTracking().SingleAsync();
        Assert.Equal(2, after.Revision);
        Assert.Contains("Reviewed copy", after.DraftJson);
        Assert.Null(after.PublishedVersionId);
        Assert.Equal(receipt, await authority.ApproveWebsiteProposalAsync(fixture.Principal, proposal.Id,
            proposal.Revision, proposal.ActionDigest, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(2, (await fixture.Db.Set<WebsiteContentState>().AsNoTracking().SingleAsync()).Revision);
        var conversation = await fixture.Db.MessageConversations.SingleAsync(x => x.Id == Guid.Parse(fixture.Scope.ConversationId));
        conversation.IsClosed = true;
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await authority.GetWebsiteProposalAsync(fixture.Principal, proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task WebsitePublicationRecoveryReadsImmutableReceiptAndRejectsForeignTenant()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        var identity = (System.Security.Claims.ClaimsIdentity)fixture.Principal.Identity!;
        var tenant = new System.Security.Claims.Claim("tid", "tenant-fixture");
        identity.AddClaim(tenant);
        var state = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Legend,
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey, Revision = 4,
            DraftJson = JsonSerializer.Serialize(new WebsiteContentDocument(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        fixture.Db.Add(state);
        await fixture.Db.SaveChangesAsync();
        var authority = fixture.NewAuthority();
        var proposed = await authority.ExecuteWebsiteToolAsync(fixture.Principal,
            new("publication-test", "legend_website_propose_publish", JsonSerializer.Serialize(new {
                website_id = state.Id.ToString("D"), expected_revision = 4 })),
            Guid.Parse(fixture.Scope.ConversationId), Guid.Parse(fixture.Scope.RequestId), CancellationToken.None);
        Assert.Contains("reviewUrl", proposed);
        var proposal = await fixture.Db.FounderAiActionAuthorizations.SingleAsync(x => x.AuthorizationKind == "WebsiteProposal");
        identity.RemoveClaim(identity.FindFirst("tid")!);
        var foreign = new System.Security.Claims.Claim("tid", "foreign-tenant"); identity.AddClaim(foreign);
        Assert.Null(await authority.GetWebsiteProposalAsync(fixture.Principal, proposal.Id, CancellationToken.None));
        identity.RemoveClaim(identity.FindFirst("tid")!); identity.AddClaim(tenant);
        // Simulate a committed publication followed by a lost action receipt.
        proposal.State = "OutcomeUnknown"; proposal.IdempotencyKey = Guid.NewGuid().ToString("D");
        fixture.Db.Add(new WebsiteContentVersion { Id = proposal.Id, StateId = state.Id, Revision = 5,
            ActorUserId = FounderId, DocumentJson = state.DraftJson });
        state.Revision = 5; state.PublishedVersionId = proposal.Id;
        await fixture.Db.SaveChangesAsync();
        var result = await authority.ApproveWebsiteProposalAsync(fixture.Principal, proposal.Id, proposal.Revision,
            proposal.ActionDigest, Guid.NewGuid(), CancellationToken.None);
        using var receipt = JsonDocument.Parse(result);
        Assert.True(receipt.RootElement.TryGetProperty("ok", out var succeeded) && succeeded.GetBoolean(), result);
        Assert.True(receipt.RootElement.GetProperty("result").GetProperty("found").GetBoolean());
        Assert.Equal(1, await fixture.Db.Set<WebsiteContentVersion>().CountAsync());
        Assert.Equal(5, (await fixture.Db.Set<WebsiteContentState>().AsNoTracking().SingleAsync()).Revision);
        Assert.Equal("Completed", (await fixture.Db.FounderAiActionAuthorizations.AsNoTracking().SingleAsync(x => x.Id == proposal.Id)).State);
    }
}
