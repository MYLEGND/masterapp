using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Analytics;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace Infrastructure.Commerce;

public sealed record CommerceSignalContext(
    Guid CommerceBusinessId,
    Guid? AgentTrackingProfileId,
    Guid? WebsiteContentVersionId,
    string SiteKey,
    string BusinessKey,
    string StoreName,
    string EventSourceUrl,
    string? SessionId = null,
    string? VisitorId = null,
    string? Referrer = null,
    string? UserAgent = null,
    string? ClientIpAddress = null,
    string? Fbclid = null,
    string? Oppref = null,
    string? Fbc = null,
    string? Fbp = null,
    DateTime? EventUtc = null,
    string? WebsiteBindingId = null,
    Guid? OrderId = null,
    string? PurchaseId = null,
    long? OrderValueCents = null,
    string Currency = "USD",
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmId = null,
    string? UtmContent = null,
    string? MetaCampaignId = null,
    string? MetaAdSetId = null,
    string? MetaAdId = null,
    string? Obref = null);

public sealed record CommerceSignalCustomer(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    string? City,
    string? State,
    string? PostalCode);

public sealed record CommerceSignalProduct(
    string? ProductId,
    string? ProductName,
    string? ProductSlug,
    string? Size,
    int Quantity,
    int ValueCents);

/// <summary>
/// Canonical analytics producer for ecommerce outcomes. Signals are derived by the shared bridge; it does not send
/// to Meta directly; the existing MetaSignalOutcomeDispatcherHostedService is
/// the sole delivery authority.
/// </summary>
public sealed class CommerceSignalService(MasterAppDbContext db)
{
    public async Task<bool> RecordAsync(
        string eventName,
        string stableIdentity,
        CommerceSignalContext context,
        CommerceSignalProduct? product = null,
        CommerceSignalCustomer? customer = null,
        string? orderNumber = null,
        IReadOnlyList<CommerceSignalProduct>? items = null,
        CancellationToken ct = default)
    {
        if (!AnalyticsEventCatalog.TryGet(eventName, out var definition) || !definition.AllowServer ||
            !MarketingConversionDestinationCatalog.TryGet(eventName, out _))
            throw new InvalidOperationException("Commerce outcomes must use the canonical confirmed-event catalog.");

        var identity = Normalize(stableIdentity);
        if (identity.Length == 0) throw new ArgumentException("A stable commerce event identity is required.", nameof(stableIdentity));

        var dedupe = $"commerce:{context.CommerceBusinessId:N}:{eventName.ToLowerInvariant()}:{identity}";
        if (dedupe.Length > 220) dedupe = dedupe[..220];

        var eventId = "commerce_" + StableToken(dedupe);
        if (eventId.Length > 120) eventId = eventId[..120];

        var analyticsClientEventId = StableGuid(dedupe);
        var existing = await db.AnalyticsEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ClientEventId == analyticsClientEventId, ct);
        if (existing is not null)
        {
            return false;
        }

        var isProtectOwner = string.Equals(context.SiteKey, WebsiteEditorSiteKeys.Protect, StringComparison.OrdinalIgnoreCase);
        var isLegendOwner = string.Equals(context.SiteKey, WebsiteEditorSiteKeys.Legend, StringComparison.OrdinalIgnoreCase);
        var typedBusinessId = isProtectOwner || isLegendOwner ? (Guid?)null : context.CommerceBusinessId;
        var typedAgentId = isProtectOwner ? context.AgentTrackingProfileId : null;
        if (isProtectOwner && !typedAgentId.HasValue)
            throw new InvalidOperationException("A Protect commerce signal requires the canonical agent marketing owner.");

        var payload = new
        {
            siteKey = context.SiteKey,
            businessType = "Ecommerce",
            reportingOwner = context.BusinessKey,
            commerceBusinessId = context.CommerceBusinessId,
            analyticsOwnerCommerceBusinessId = typedBusinessId,
            analyticsOwnerAgentTrackingProfileId = typedAgentId,
            websiteContentVersionId = context.WebsiteContentVersionId,
            sourceUrl = context.EventSourceUrl,
            sourceHost = Uri.TryCreate(context.EventSourceUrl, UriKind.Absolute, out var sourceUri) ? sourceUri.Host : null,
            sourcePath = Uri.TryCreate(context.EventSourceUrl, UriKind.Absolute, out sourceUri) ? sourceUri.AbsolutePath : null,
            businessKey = context.BusinessKey,
            storeName = context.StoreName,
            originalEventIdentity = identity,
            orderNumber,
            orderId = context.OrderId,
            purchaseId = context.PurchaseId,
            websiteBindingId = context.WebsiteBindingId,
            currency = context.Currency,
            valueCents = context.OrderValueCents ?? items?.Sum(x => (long)Math.Max(0, x.ValueCents)) ?? product?.ValueCents ?? 0,
            productId = product?.ProductId,
            productName = product?.ProductName,
            productSlug = product?.ProductSlug,
            size = product?.Size,
            quantity = items?.Sum(x => Math.Max(0, x.Quantity)) ?? product?.Quantity ?? 0,
            items = items?.Select(x => new
            {
                x.ProductId,
                x.ProductName,
                x.ProductSlug,
                x.Size,
                x.Quantity,
                x.ValueCents
            }).ToArray(),
            customer = customer is null ? null : new
            {
                firstName = customer.FirstName,
                lastName = customer.LastName,
                email = customer.Email,
                phone = customer.Phone,
                city = customer.City,
                state = customer.State,
                postalCode = customer.PostalCode
            },
            fbclid = context.Fbclid,
            oppref = OpenAiClickReference.Normalize(context.Oppref),
            obref = OpenAiBrowserReference.Normalize(context.Obref),
            fbc = context.Fbc,
            fbp = context.Fbp,
            sourceClientIpAddress = context.ClientIpAddress,
            sourceClientUserAgent = context.UserAgent
        };

        var now = context.EventUtc ?? DateTime.UtcNow;
        var pageKey = "commerce_" + eventName.ToLowerInvariant();
        var unifiedContext = new UnifiedEventContext
        {
            SiteKey = context.SiteKey,
            CommerceBusinessId = typedBusinessId,
            WebsiteContentVersionId = context.WebsiteContentVersionId,
            WebsiteBindingId = context.WebsiteBindingId,
            EventId = eventId,
            EventName = eventName,
            EventCategory = definition.Category,
            EventUtc = now,
            SessionId = NormalizeNullable(context.SessionId),
            VisitorId = NormalizeNullable(context.VisitorId),
            Referrer = NormalizeNullable(context.Referrer),
            PageKey = pageKey,
            EffectivePageKey = pageKey,
            PageVariant = "store",
            PageMode = "store",
            QuoteType = "ecommerce",
            UserAgent = NormalizeNullable(context.UserAgent),
            IpAddress = NormalizeNullable(context.ClientIpAddress),
            UtmSource = NormalizeNullable(context.UtmSource),
            UtmMedium = NormalizeNullable(context.UtmMedium),
            UtmCampaign = NormalizeNullable(context.UtmCampaign),
            UtmId = NormalizeNullable(context.UtmId),
            UtmContent = NormalizeNullable(context.UtmContent),
            MetaCampaignId = NormalizeNullable(context.MetaCampaignId),
            MetaAdSetId = NormalizeNullable(context.MetaAdSetId),
            MetaAdId = NormalizeNullable(context.MetaAdId),
            Fbclid = NormalizeNullable(context.Fbclid),
            Oppref = OpenAiClickReference.Normalize(context.Oppref),
            Obref = OpenAiBrowserReference.Normalize(context.Obref),
            Fbc = NormalizeNullable(context.Fbc),
            Fbp = NormalizeNullable(context.Fbp),
            AgentTrackingProfileId = typedAgentId,
            Environment = ResolveEnvironment(context.EventSourceUrl),
            Host = Uri.TryCreate(context.EventSourceUrl, UriKind.Absolute, out var uri) ? uri.Host : null,
            IsBrowserSignal = false,
            IsServerAuthority = true,
            MetaServerAuthorityEligible = true,
            Metadata = payload
        };

        var analyticsRow = UnifiedEventMapper.ToAnalytics(unifiedContext);
        analyticsRow.EventId = analyticsClientEventId;
        analyticsRow.ClientEventId = analyticsClientEventId;
        analyticsRow.Url = context.EventSourceUrl;
        analyticsRow.Path = Uri.TryCreate(context.EventSourceUrl, UriKind.Absolute, out var analyticsUri)
            ? analyticsUri.AbsolutePath : null;
        analyticsRow.TrackingVersion = "commerce-server-authority-v1";
        analyticsRow.SchemaVersion = 2;
        var metadata = System.Text.Json.JsonSerializer.SerializeToNode(payload)!.AsObject();
        metadata["canonicalOutcomeEventId"] = eventId;
        metadata["canonicalDeduplicationKey"] = dedupe;
        metadata["upstreamMetaEventId"] = eventId;
        metadata["metaDeduplicationKey"] = dedupe;
        metadata["stepNumber"] = eventName switch { "AddToCart" => 5, "InitiateCheckout" => 6, "Purchase" => 8, _ => 4 };
        metadata["stepName"] = eventName.ToLowerInvariant();
        metadata["scoreTier"] = eventName == "Purchase" ? "Purchase" : "Commerce";
        var score = eventName == "Purchase" ? 500 : 250;
        metadata["intentScore"] = score;
        metadata["engagementScore"] = score;
        metadata["qualificationScore"] = score;
        metadata["frictionScore"] = 0;
        metadata["totalSignalScore"] = score;
        analyticsRow.MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
            eventName, null, context.SessionId, metadata, false, true, true, false,
            "CommercePurchaseBridge");
        UnifiedAnalyticsWriter.Write(db, analyticsRow);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(analyticsRow).State = EntityState.Detached;
            existing = await db.AnalyticsEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ClientEventId == analyticsClientEventId, ct);
            if (existing is null) throw;
            return false;
        }
        return true;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static string? NormalizeNullable(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string StableToken(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static string ResolveEnvironment(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)))
            return "development";
        return "production";
    }
}
