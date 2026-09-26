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
    string? Fbc = null,
    string? Fbp = null);

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
/// Central server-authority writer for ecommerce Meta outcomes. It does not send
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
        if (!MetaSignalEventCatalog.TryGet(eventName, out var definition) ||
            !MetaSignalEventCatalog.IsServerAuthorityEvent(eventName) ||
            !definition.AllowServerForward)
            throw new InvalidOperationException("Commerce events must use the canonical server-authority Meta catalog.");

        var identity = Normalize(stableIdentity);
        if (identity.Length == 0) throw new ArgumentException("A stable commerce event identity is required.", nameof(stableIdentity));

        var dedupe = $"commerce:{context.CommerceBusinessId:N}:{eventName.ToLowerInvariant()}:{identity}";
        if (dedupe.Length > 220) dedupe = dedupe[..220];

        var eventId = "commerce_" + StableToken(dedupe);
        if (await db.MetaSignalEvents.AsNoTracking()
            .AnyAsync(x => x.EventId == eventId || x.MetaDeduplicationKey == dedupe, ct))
            return false;
        if (eventId.Length > 120) eventId = eventId[..120];

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
            orderNumber,
            currency = "USD",
            valueCents = product?.ValueCents ?? items?.Sum(x => Math.Max(0, x.ValueCents)) ?? 0,
            productId = product?.ProductId,
            productName = product?.ProductName,
            productSlug = product?.ProductSlug,
            size = product?.Size,
            quantity = product?.Quantity ?? items?.Sum(x => Math.Max(0, x.Quantity)) ?? 0,
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
            fbc = context.Fbc,
            fbp = context.Fbp,
            sourceClientIpAddress = context.ClientIpAddress,
            sourceClientUserAgent = context.UserAgent
        };

        var now = DateTime.UtcNow;
        var pageKey = "commerce_" + eventName.ToLowerInvariant();
        var row = UnifiedMetaSignalWriter.Create(new UnifiedEventContext
        {
            SiteKey = context.SiteKey,
            CommerceBusinessId = typedBusinessId,
            WebsiteContentVersionId = context.WebsiteContentVersionId,
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
            Fbclid = NormalizeNullable(context.Fbclid),
            Fbc = NormalizeNullable(context.Fbc),
            Fbp = NormalizeNullable(context.Fbp),
            AgentTrackingProfileId = typedAgentId,
            Environment = ResolveEnvironment(context.EventSourceUrl),
            Host = Uri.TryCreate(context.EventSourceUrl, UriKind.Absolute, out var uri) ? uri.Host : null,
            IsBrowserSignal = false,
            IsServerAuthority = true,
            MetaServerAuthorityEligible = true,
            Metadata = payload
        }, row =>
        {
            row.TrafficType = "ecommerce";
            row.FunnelStep = eventName switch
            {
                "AddToCart" => 5,
                "InitiateCheckout" => 6,
                "Purchase" => 8,
                _ => 4
            };
            row.StepName = eventName.ToLowerInvariant();
            row.IntentScore = eventName == "Purchase" ? 500 : 250;
            row.EngagementScore = eventName == "Purchase" ? 500 : 250;
            row.QualificationScore = eventName == "Purchase" ? 500 : 250;
            row.FrictionScore = 0;
            row.TotalSignalScore = eventName == "Purchase" ? 500 : 250;
            row.ScoreTier = eventName == "Purchase" ? "Purchase" : "Commerce";
            row.MetaBrowserSent = false;
            row.MetaServerSent = false;
            row.MetaDeduplicationKey = dedupe;
            row.MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
                eventName,
                leadId: null,
                sessionId: context.SessionId,
                payload: payload,
                isBrowserSignal: false,
                isServerAuthority: true,
                metaServerAuthorityEligible: true,
                metaSingleTruthDispatchEligible: true,
                metaPipelineOrigin: "CommercePurchaseBridge");
        });

        UnifiedMetaSignalWriter.Write(db, row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (await db.MetaSignalEvents.AsNoTracking()
                .AnyAsync(x => x.MetaDeduplicationKey == dedupe || x.EventId == eventId, ct))
                return false;
            throw;
        }
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
