using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ProtectWebsite.Controllers;

[ApiController]
[Route("api/website-content")]
public sealed class WebsiteContentController : ControllerBase
{
    private const int MaxElements = 600;
    private const int MaxExtras = 120;
    private const int MaxTextLength = 12000;
    private const int MaxImageDataUrlLength = 3_500_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MasterAppDbContext _db;
    private readonly WebsiteEditorTicketProtector _tickets;
    private readonly IConfiguration _configuration;

    public WebsiteContentController(
        MasterAppDbContext db,
        WebsiteEditorTicketProtector tickets,
        IConfiguration configuration)
    {
        _db = db;
        _tickets = tickets;
        _configuration = configuration;
    }

    [HttpGet("public/{siteKey}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Public(
        string siteKey,
        [FromQuery] string? agentSlug = null,
        CancellationToken cancellationToken = default)
    {
        siteKey = NormalizeSiteKey(siteKey);
        if (siteKey.Length == 0) return NotFound();

        var ownerKey = siteKey == WebsiteEditorSiteKeys.Legend
            ? WebsiteEditorSiteKeys.GlobalOwnerKey
            : await ResolveProtectOwnerKeyAsync(agentSlug, cancellationToken);

        if (string.IsNullOrWhiteSpace(ownerKey))
            return Ok(new { siteKey, document = new WebsiteContentDocument() });

        var document = await LoadAsync(ownerKey, siteKey, cancellationToken);
        return Ok(new { siteKey, document });
    }

    [HttpGet("manage")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Manage(
        [FromQuery] string ticket,
        CancellationToken cancellationToken = default)
    {
        var resolved = _tickets.TryUnprotect(ticket);
        if (resolved is null) return Unauthorized();

        var document = await LoadAsync(resolved.OwnerUserId, resolved.SiteKey, cancellationToken);
        return Ok(new
        {
            siteKey = resolved.SiteKey,
            ownerUserId = resolved.OwnerUserId,
            agentSlug = resolved.AgentSlug,
            isFounder = resolved.IsFounder,
            document
        });
    }

    public sealed record SaveRequest(string Ticket, WebsiteContentDocument Document);

    [HttpPost("manage")]
    [RequestSizeLimit(4_500_000)]
    public async Task<IActionResult> Save(
        [FromBody] SaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Document is null) return BadRequest();
        var resolved = _tickets.TryUnprotect(request.Ticket);
        if (resolved is null) return Unauthorized();

        var sanitized = Sanitize(request.Document);
        sanitized.UpdatedUtc = DateTime.UtcNow;
        var toolId = ToolId(resolved.SiteKey);
        var ownerKey = NormalizeOwner(resolved.OwnerUserId);
        if (ownerKey.Length == 0) return BadRequest();

        var row = await _db.AgentFinanceToolStates
            .SingleOrDefaultAsync(
                x => x.AgentUserId == ownerKey && x.ToolId == toolId,
                cancellationToken);

        var json = JsonSerializer.Serialize(sanitized, JsonOptions);
        if (row is null)
        {
            row = new AgentFinanceToolState
            {
                AgentUserId = ownerKey,
                ToolId = toolId,
                JsonState = json,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            _db.AgentFinanceToolStates.Add(row);
        }
        else
        {
            row.JsonState = json;
            row.UpdatedUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, document = sanitized, savedUtc = row.UpdatedUtc });
    }

    private async Task<WebsiteContentDocument> LoadAsync(
        string ownerUserId,
        string siteKey,
        CancellationToken cancellationToken)
    {
        var ownerKey = NormalizeOwner(ownerUserId);
        var toolId = ToolId(siteKey);
        var json = await _db.AgentFinanceToolStates
            .AsNoTracking()
            .Where(x => x.AgentUserId == ownerKey && x.ToolId == toolId)
            .Select(x => x.JsonState)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(json)) return new WebsiteContentDocument();

        try
        {
            return Sanitize(JsonSerializer.Deserialize<WebsiteContentDocument>(json, JsonOptions)
                ?? new WebsiteContentDocument());
        }
        catch (JsonException)
        {
            return new WebsiteContentDocument();
        }
    }

    private async Task<string?> ResolveProtectOwnerKeyAsync(
        string? agentSlug,
        CancellationToken cancellationToken)
    {
        AgentTrackingProfile? profile = null;
        var slug = (agentSlug ?? string.Empty).Trim();

        if (slug.Length > 0)
        {
            var normalized = slug.ToLower();
            profile = await _db.AgentTrackingProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Slug.ToLower() == normalized, cancellationToken);
        }
        else
        {
            var founderUpn = (_configuration["Founder:Upn"] ?? string.Empty).Trim().ToLower();
            if (founderUpn.Length > 0)
            {
                profile = await _db.AgentTrackingProfiles
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.AgentUpn.ToLower() == founderUpn, cancellationToken);
            }
        }

        return profile is null ? null : NormalizeOwner(profile.AgentUserId);
    }

    private static string NormalizeSiteKey(string? value)
    {
        var key = (value ?? string.Empty).Trim().ToLowerInvariant();
        return key is WebsiteEditorSiteKeys.Protect or WebsiteEditorSiteKeys.Legend ? key : string.Empty;
    }

    private static string NormalizeOwner(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string ToolId(string siteKey)
        => WebsiteEditorSiteKeys.ToolPrefix + siteKey;

    private static WebsiteContentDocument Sanitize(WebsiteContentDocument source)
    {
        var clean = new WebsiteContentDocument { Version = 1 };

        foreach (var pair in source.Elements.Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0 || pair.Value is null) continue;
            clean.Elements[id] = SanitizeElement(pair.Value);
        }

        foreach (var pair in source.SectionOrder.Take(MaxElements))
        {
            var id = SanitizeId(pair.Key);
            if (id.Length == 0) continue;
            clean.SectionOrder[id] = Math.Clamp(pair.Value, 0, MaxElements);
        }

        foreach (var extra in source.Extras.Take(MaxExtras))
        {
            if (extra is null) continue;
            var id = SanitizeId(extra.Id);
            var sectionId = SanitizeId(extra.SectionId);
            var type = (extra.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || sectionId.Length == 0 || type is not ("text" or "image")) continue;
            clean.Extras.Add(new WebsiteExtraComponent
            {
                Id = id,
                SectionId = sectionId,
                Type = type,
                Text = type == "text" ? ClampText(extra.Text) : null,
                ImageDataUrl = type == "image" ? SanitizeImage(extra.ImageDataUrl) : null,
                Style = SanitizeStyle(extra.Style)
            });
        }

        clean.Theme = SanitizeTheme(source.Theme);
        clean.UpdatedUtc = source.UpdatedUtc;
        return clean;
    }

    private static WebsiteElementOverride SanitizeElement(WebsiteElementOverride source) => new()
    {
        Text = ClampText(source.Text),
        ImageDataUrl = SanitizeImage(source.ImageDataUrl),
        Hidden = source.Hidden,
        Style = SanitizeStyle(source.Style)
    };

    private static WebsiteThemeOverride SanitizeTheme(WebsiteThemeOverride? source)
    {
        source ??= new WebsiteThemeOverride();
        return new WebsiteThemeOverride
        {
            Navy = SanitizeHex(source.Navy),
            NavyDeep = SanitizeHex(source.NavyDeep),
            Gold = SanitizeHex(source.Gold),
            GoldStrong = SanitizeHex(source.GoldStrong),
            Surface = SanitizeHex(source.Surface)
        };
    }

    private static string? SanitizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.Length != 7 || candidate[0] != '#') return null;
        for (var i = 1; i < candidate.Length; i++)
        {
            if (!Uri.IsHexDigit(candidate[i])) return null;
        }
        return candidate.ToLowerInvariant();
    }

    private static WebsiteStyleOverride SanitizeStyle(WebsiteStyleOverride? source)
    {
        source ??= new WebsiteStyleOverride();
        var align = (source.TextAlign ?? string.Empty).Trim().ToLowerInvariant();
        if (align is not ("left" or "center" or "right" or "start" or "end" or "justify")) align = string.Empty;
        var objectPosition = (source.ObjectPosition ?? string.Empty).Trim().ToLowerInvariant();
        if (objectPosition is not ("left" or "center" or "right" or "top" or "bottom"))
            objectPosition = string.Empty;

        return new WebsiteStyleOverride
        {
            TextAlign = align.Length == 0 ? null : align,
            FontScale = source.FontScale > 0 ? source.FontScale : null,
            WidthPercent = source.WidthPercent > 0 ? source.WidthPercent : null,
            PaddingTop = source.PaddingTop >= 0 ? source.PaddingTop : null,
            PaddingBottom = source.PaddingBottom >= 0 ? source.PaddingBottom : null,
            ObjectPosition = objectPosition.Length == 0 ? null : objectPosition
        };
    }

    private static string? ClampText(string? value)
    {
        if (value is null) return null;
        var normalized = value.Replace("\0", string.Empty).Trim();
        return normalized.Length <= MaxTextLength
            ? normalized
            : normalized[..MaxTextLength];
    }

    private static string? SanitizeImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > MaxImageDataUrlLength) return null;
        if (normalized.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase))
            return normalized;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
            return normalized;
        return null;
    }

    private static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().Take(160)
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            .ToArray();
        return new string(chars);
    }
}
