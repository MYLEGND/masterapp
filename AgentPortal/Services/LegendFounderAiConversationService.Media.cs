using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace AgentPortal.Services;

public sealed partial class LegendFounderAiConversationService
{
    private async Task<bool> ValidateBusinessImageSelectionAsync(IServiceProvider services, ClaimsPrincipal principal,
        string workspace, Guid[] references, CancellationToken ct)
    {
        var owner = await AuthorizeBusinessMediaAsync(services, principal, workspace, ct);
        var db = services.GetRequiredService<MasterAppDbContext>();
        var count = await db.Set<WebsiteMediaAsset>().CountAsync(x => x.OwnerKey == owner && references.Contains(x.Id) &&
            x.SizeBytes > 0 && x.SizeBytes <= 5_000_000 &&
            (x.ContentType == "image/png" || x.ContentType == "image/jpeg" || x.ContentType == "image/webp"), ct);
        return count == references.Length;
    }
    private async Task<string> AuthorizeBusinessMediaAsync(IServiceProvider services, ClaimsPrincipal principal,
        string workspace, CancellationToken ct)
    {
        var db = services.GetRequiredService<MasterAppDbContext>();
        var (actor, owner) = await AuthorizeBusinessAsync(principal, workspace, db, ct, "website");
        if (owner.CommerceBusinessId is { } businessId) return WebsiteEditorSiteKeys.BusinessOwnerKey(businessId);
        // The workspace guard has verified this is the actor's own active agent.
        return Shared.Auth.IdentityKey.Normalize(actor.UserId);
    }

    public async Task<IReadOnlyList<object>> ListBusinessMediaAsync(ClaimsPrincipal principal, string workspace, CancellationToken ct)
    {
        await using var scope = _historyScopes.CreateAsyncScope();
        var owner = await AuthorizeBusinessMediaAsync(scope.ServiceProvider, principal, workspace, ct);
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var assets = await db.Set<WebsiteMediaAsset>().AsNoTracking().Where(x => x.OwnerKey == owner &&
            (x.ContentType == "image/png" || x.ContentType == "image/jpeg" || x.ContentType == "image/webp"))
            .OrderByDescending(x => x.CreatedUtc).Take(100).Select(x => new { x.Id, x.ContentType, x.SizeBytes, x.CreatedUtc }).ToListAsync(ct);
        return assets.Cast<object>().ToArray();
    }

    public async Task<(WebsiteMediaAsset Asset, Stream Content)?> OpenBusinessMediaAsync(ClaimsPrincipal principal,
        string workspace, Guid id, CancellationToken ct)
    {
        await using var scope = _historyScopes.CreateAsyncScope();
        var owner = await AuthorizeBusinessMediaAsync(scope.ServiceProvider, principal, workspace, ct);
        return await scope.ServiceProvider.GetRequiredService<WebsiteMediaService>().OpenAsync(owner, id, ct);
    }

    private async Task<WebsiteMediaAsset> CreateBusinessImageAsync(IServiceProvider services, ClaimsPrincipal principal,
        MarketingOwnerScope owner, BusinessAssistantConfiguration config, Guid revision, string prompt, Guid[] references, CancellationToken ct)
    {
        if (!config.ImagesEnabled || config.ImageModel is null || config.ImageQuality is null || prompt.Length > 4000 || references.Length > 4)
            throw new ArgumentException("Image configuration or bounded input is unavailable.");
        var mediaOwner = await AuthorizeBusinessMediaAsync(services, principal, owner.Key, ct);
        var media = services.GetRequiredService<WebsiteMediaService>();
        using var request = new HttpRequestMessage(HttpMethod.Post, references.Length == 0 ? "v1/images/generations" : "v1/images/edits");
        var safetyId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner.Key)));
        if (references.Length == 0)
            request.Content = JsonContent.Create(new { model = config.ImageModel, prompt, quality = config.ImageQuality,
                size = "1024x1024", n = 1, output_format = "png", safety_identifier = safetyId });
        else
        {
            var form = new MultipartFormDataContent();
            request.Content = form;
            foreach (var field in new Dictionary<string, string> { ["model"] = config.ImageModel, ["prompt"] = prompt,
                ["quality"] = config.ImageQuality, ["size"] = "1024x1024", ["n"] = "1", ["output_format"] = "png", ["safety_identifier"] = safetyId })
                form.Add(new StringContent(field.Value), field.Key);
            foreach (var id in references)
            {
                var asset = await media.OpenAsync(mediaOwner, id, ct) ?? throw new ArgumentException("Reference image is unavailable in this workspace.");
                await using var source = asset.Content;
                if (asset.Asset.ContentType is not ("image/png" or "image/jpeg" or "image/webp") || asset.Asset.SizeBytes > 5_000_000)
                    throw new ArgumentException("Reference image format or size is unsupported.");
                var bytes = await ReadBoundedMediaAsync(source, 5_000_000, ct);
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(asset.Asset.ContentType);
                form.Add(part, "image[]", "reference" + (asset.Asset.ContentType == "image/png" ? ".png" : asset.Asset.ContentType == "image/jpeg" ? ".jpg" : ".webp"));
            }
        }
        var key = await services.GetRequiredService<MarketingConnectionStore>().GetAssistantCredentialAsync(owner, config.Provider, revision, ct)
            ?? throw new InvalidOperationException("Image provider connection changed or was revoked.");
        await AuthorizeBusinessMediaAsync(services, principal, owner.Key, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("OpenAI-Project", config.ProjectId);
        if (config.OrganizationId is not null) request.Headers.Add("OpenAI-Organization", config.OrganizationId);
        var client = _httpClientFactory.CreateClient("OpenAI");
        client.Timeout = Timeout.InfiniteTimeSpan;
        // One admitted provider attempt; interruption never permits blind replay.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Image provider rejected the request.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var payload = await ReadBoundedMediaAsync(body, 8_000_000, ct);
        using var document = JsonDocument.Parse(payload);
        var data = document.RootElement.GetProperty("data");
        if (data.GetArrayLength() != 1) throw new InvalidOperationException("Expected exactly one generated image.");
        var image = Convert.FromBase64String(data[0].GetProperty("b64_json").GetString() ?? "");
        if (image.Length == 0 || image.Length > 5_000_000) throw new InvalidOperationException("Generated image exceeds media limits.");
        // Recheck current permission before persistence; never publish from generation.
        if (await AuthorizeBusinessMediaAsync(services, principal, owner.Key, ct) != mediaOwner)
            throw new InvalidOperationException("Media ownership changed.");
        return await media.StoreImageAsync(mediaOwner, "LEGEND AI generated image", image, ct);
    }

    internal static async Task<byte[]> ReadBoundedMediaAsync(Stream source, int maximumBytes, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + count > maximumBytes) throw new InvalidOperationException("Media exceeds the supported size.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
