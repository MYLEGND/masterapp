using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed partial class ParfaitCustomerAutomationService
{
    private static readonly TimeSpan CartLeadRetentionWindow = TimeSpan.FromDays(45);
    private static readonly object StateLock = new();

    private readonly IConfiguration _configuration;
    private readonly ParfaitOrderService _orders;
    private readonly ParfaitProductService _products;
    private readonly Infrastructure.Data.MasterAppDbContext _db;
    private readonly CommerceStoreContextService? _stores;
    private readonly ILogger<ParfaitCustomerAutomationService> _logger;

    public ParfaitCustomerAutomationService(
        ParfaitStoragePaths storagePaths,
        IConfiguration configuration,
        ParfaitOrderService orders,
        ParfaitProductService products,
        Infrastructure.Data.MasterAppDbContext db,
        CommerceStoreContextService? stores = null,
        ILogger<ParfaitCustomerAutomationService>? logger = null)
    {
        _configuration = configuration;
        _orders = orders;
        _products = products;
        _db = db;
        _stores = stores;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ParfaitCustomerAutomationService>.Instance;
    }


    public ParfaitAutomationWorkspaceViewModel GetWorkspaceViewModel() =>
        GetWorkspaceViewModel(_products.GetDefaultBusinessId());

    public ParfaitAutomationWorkspaceViewModel GetWorkspaceViewModel(Guid businessId)
    {
        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            CleanupUnsafe(store, DateTime.UtcNow);

            var workflowDueCounts = BuildDueCountLookup(store, DateTime.UtcNow, businessId);
            var since = DateTime.UtcNow.AddDays(-7);
            var sentLast7Days = EmailDeliveries(businessId).Count(dispatch => dispatch.Status == "accepted" && dispatch.SentUtc >= since);

            var customers = BuildCustomers(store, businessId);
            var activity = store.Dispatches
                .OrderByDescending(dispatch => dispatch.OccurredUtc)
                .Take(16)
                .Select(dispatch => new ParfaitAutomationDispatchActivityViewModel
                {
                    WorkflowName = dispatch.WorkflowName,
                    TriggerLabel = dispatch.Shipment is not null ? "Shipment update" : ParfaitAutomationTriggerTypes.ToLabel(dispatch.TriggerType),
                    RecipientLabel = string.IsNullOrWhiteSpace(dispatch.RecipientFirstName)
                        ? dispatch.RecipientEmail
                        : $"{dispatch.RecipientFirstName} · {dispatch.RecipientEmail}",
                    Subject = dispatch.Subject,
                    Status = dispatch.Status,
                    Tone = DispatchTone(dispatch.Status),
                    Detail = string.IsNullOrWhiteSpace(dispatch.ErrorMessage)
                        ? (string.IsNullOrWhiteSpace(dispatch.OrderNumber)
                            ? "Accepted by the email provider; inbox delivery is not confirmed."
                            : $"Order {dispatch.OrderNumber}")
                        : dispatch.ErrorMessage,
                    OccurredUtc = dispatch.OccurredUtc
                })
                .ToList();

            return new ParfaitAutomationWorkspaceViewModel
            {
                EmailPostalAddress = store.EmailPostalAddress,
                EmailReadinessMessage = string.IsNullOrWhiteSpace(store.EmailPostalAddress)
                    ? "Add the sender mailing address to prepare promotional sending."
                    : TryResolvePublicBaseUrl(businessId) is null ? "Publish and verify this store's public address to prepare promotional sending."
                    : "Eligible workflows use this store's opted-in audience. Provider acceptance and delivery remain distinct.",
                SettingsUpdatedTicks = store.UpdatedUtc.Ticks,
                Workflows = store.Workflows
                    .OrderBy(workflow => workflow.TriggerType)
                    .ThenBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(workflow => new ParfaitAutomationWorkflowCardViewModel
                    {
                        Id = workflow.Id,
                        UpdatedTicks = workflow.UpdatedUtc.Ticks,
                        Name = workflow.Name,
                        TriggerType = workflow.TriggerType,
                        TriggerLabel = ParfaitAutomationTriggerTypes.ToLabel(workflow.TriggerType),
                        DelayAmount = workflow.DelayAmount,
                        DelayUnit = workflow.DelayUnit,
                        DelayLabel = BuildDelayLabel(workflow.DelayAmount, workflow.DelayUnit),
                        Subject = workflow.Subject,
                        Headline = workflow.Headline,
                        Body = workflow.Body,
                        CtaLabel = workflow.CtaLabel,
                        CtaUrl = workflow.CtaUrl,
                        DiscountCode = workflow.DiscountCode,
                        IsActive = workflow.IsActive,
                        DueNowCount = workflowDueCounts.TryGetValue(workflow.Id, out var dueNow) ? dueNow : 0,
                        SentLast7Days = EmailDeliveries(businessId).Count(dispatch => dispatch.CanonicalEventId.StartsWith(workflow.Id.ToString("N") + ":")
                            && dispatch.Status == "accepted" && dispatch.SentUtc >= since),
                        LastSentUtc = EmailDeliveries(businessId).Where(dispatch => dispatch.CanonicalEventId.StartsWith(workflow.Id.ToString("N") + ":")
                            && dispatch.Status == "accepted").OrderByDescending(dispatch => dispatch.SentUtc).Select(dispatch => dispatch.SentUtc).FirstOrDefault(),
                        Tone = workflow.IsActive
                            ? workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart ? "warning" : "success"
                            : "muted"
                    })
                    .ToList(),
                Customers = customers,
                Activity = activity,
                DiscountOptions = BuildDiscountOptions(businessId),
                NewWorkflow = new ParfaitAutomationWorkflowEditorInput(),
                ActiveWorkflowCount = store.Workflows.Count(workflow => workflow.IsActive),
                AudienceCount = customers.Count,
                OpenCartLeadCount = store.CartLeads.Count(lead => !lead.IsConverted),
                SentLast7Days = sentLast7Days
            };
        }
    }

    public void SaveWorkflow(ParfaitAutomationWorkflowEditorInput input) =>
        SaveWorkflow(_products.GetDefaultBusinessId(), input);

    public void SaveWorkflow(Guid businessId, ParfaitAutomationWorkflowEditorInput input)
    {
        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            CleanupUnsafe(store, DateTime.UtcNow);

            var workflow = NormalizeWorkflow(input);
            if (!(workflow.CtaUrl.StartsWith("/", StringComparison.Ordinal) && !workflow.CtaUrl.StartsWith("//", StringComparison.Ordinal))
                && (!Uri.TryCreate(workflow.CtaUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Use a store path beginning with / or a secure https:// link.");
            var existingIndex = store.Workflows.FindIndex(item => item.Id == workflow.Id);
            if (existingIndex >= 0)
            {
                var previous = store.Workflows[existingIndex];
                if (input.ExpectedUpdatedTicks != previous.UpdatedUtc.Ticks)
                    throw new InvalidOperationException("This workflow changed. Reload and review the latest version before saving.");
                workflow.CreatedUtc = previous.CreatedUtc;
                workflow.EligibleFromUtc = workflow.IsActive
                    ? (previous.IsActive ? previous.EligibleFromUtc : DateTime.UtcNow) : null;
                store.Workflows[existingIndex] = workflow;
            }
            else
            {
                workflow.EligibleFromUtc = workflow.IsActive ? DateTime.UtcNow : null;
                store.Workflows.Add(workflow);
            }

            SaveStoreUnsafe(store, businessId);
        }
    }

    public void DeleteWorkflow(Guid id, long expectedUpdatedTicks) =>
        DeleteWorkflow(_products.GetDefaultBusinessId(), id, expectedUpdatedTicks);

    public void DeleteWorkflow(Guid businessId, Guid id, long expectedUpdatedTicks)
    {
        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            var workflow = store.Workflows.SingleOrDefault(item => item.Id == id);
            if (workflow is null) return;
            if (workflow.UpdatedUtc.Ticks != expectedUpdatedTicks)
                throw new InvalidOperationException("This workflow changed. Reload and review the latest version before deleting.");
            store.Workflows.Remove(workflow);
            SaveStoreUnsafe(store, businessId);
        }
    }

    public void CaptureCheckoutLead(ParfaitAutomationCheckoutLeadCaptureRequest request, ParfaitCartQuoteResponse quote) =>
        CaptureCheckoutLead(_products.GetDefaultBusinessId(), request, quote);

    public void CaptureCheckoutLead(Guid businessId, ParfaitAutomationCheckoutLeadCaptureRequest request, ParfaitCartQuoteResponse quote)
    {
        var checkoutAttemptId = CleanOptional(request.CheckoutAttemptId);
        var email = NormalizeEmail(request.Customer.Email);
        if (string.IsNullOrWhiteSpace(checkoutAttemptId) || string.IsNullOrWhiteSpace(email))
            return;
        if (LatestMarketingConsent(businessId, email) is { MarketingEmailConsent: false }) return;

        var items = quote.Items
            .Where(item => item.IsAvailable && item.Quantity > 0)
            .Select(item => new ParfaitValidatedCartItem
            {
                Id = item.Id,
                Name = item.Name,
                Slug = item.Slug,
                Size = item.Size,
                Quantity = item.Quantity,
                UnitPriceCents = item.UnitPriceCents,
                CompareAtPriceCents = item.CompareAtPriceCents,
                ImageUrl = item.ImageUrl
            })
            .ToList();

        if (items.Count == 0)
            return;

        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            CleanupUnsafe(store, DateTime.UtcNow);

            var lead = store.CartLeads.FirstOrDefault(item =>
                item.CheckoutAttemptId.Equals(checkoutAttemptId, StringComparison.OrdinalIgnoreCase));

            if (lead is null)
            {
                lead = new ParfaitAutomationCartLeadRecord
                {
                    CheckoutAttemptId = checkoutAttemptId,
                    FirstCapturedUtc = DateTime.UtcNow
                };
                store.CartLeads.Add(lead);
            }

            lead.Email = email;
            lead.FirstName = CleanOptional(request.Customer.FirstName) ?? lead.FirstName;
            lead.LastName = CleanOptional(request.Customer.LastName) ?? lead.LastName;
            lead.Phone = CleanOptional(request.Customer.Phone) ?? lead.Phone;
            lead.Items = items;
            lead.SubtotalCents = quote.SubtotalCents;
            lead.DiscountCents = quote.DiscountCents;
            lead.ShippingCents = quote.ShippingCents;
            lead.TaxCents = quote.TaxCents;
            lead.TotalCents = quote.TotalCents;
            lead.DiscountCode = CleanOptional(quote.DiscountCode) ?? "";
            lead.LastActivityUtc = DateTime.UtcNow;
            lead.IsConverted = false;
            lead.ConvertedOrderNumber = null;

            SaveStoreUnsafe(store, businessId);
        }
    }

    public void MarkOrderConverted(ParfaitOrderRecord order) =>
        MarkOrderConverted(_products.GetDefaultBusinessId(), order);

    public void MarkOrderConverted(Guid businessId, ParfaitOrderRecord order)
    {
        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            CleanupUnsafe(store, DateTime.UtcNow);

            var checkoutAttemptId = CleanOptional(order.CheckoutAttemptId);
            var email = NormalizeEmail(order.Email);

            var candidates = store.CartLeads
                .Where(lead => !lead.IsConverted)
                .Where(lead =>
                    (!string.IsNullOrWhiteSpace(checkoutAttemptId) &&
                     lead.CheckoutAttemptId.Equals(checkoutAttemptId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(email) &&
                        lead.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var lead in candidates)
            {
                lead.IsConverted = true;
                lead.ConvertedOrderNumber = order.OrderNumber;
            }

            SaveStoreUnsafe(store, businessId);
        }
    }

    public IReadOnlyList<ParfaitAutomationDispatchCandidate> GetDueDispatchCandidates() =>
        GetDueDispatchCandidates(_products.GetDefaultBusinessId());

    public IReadOnlyList<ParfaitAutomationDispatchCandidate> GetDueDispatchCandidates(Guid businessId)
    {
        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            CleanupUnsafe(store, DateTime.UtcNow);

            var now = DateTime.UtcNow;
            var orders = _orders.GetAllOrders(businessId);
            var candidates = GetShipmentCandidates(businessId).ToList();
            if (string.IsNullOrWhiteSpace(store.EmailPostalAddress) || TryResolvePublicBaseUrl(businessId) is null) return candidates;

            foreach (var workflow in store.Workflows.Where(item => item.IsActive && item.EligibleFromUtc.HasValue))
            {
                if (workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart)
                {
                    foreach (var lead in store.CartLeads.Where(item => !item.IsConverted))
                    {
                        if (string.IsNullOrWhiteSpace(lead.Email) || lead.Items.Count == 0
                            || lead.LastActivityUtc < workflow.EligibleFromUtc || !HasPromotionalConsent(businessId, lead.Email))
                            continue;

                        var dueAt = lead.LastActivityUtc.Add(BuildDelay(workflow.DelayAmount, workflow.DelayUnit));
                        if (dueAt > now)
                            continue;

                        var triggerKey = $"{lead.Id:N}:{lead.LastActivityUtc.Ticks}";
                        if (!CanAttemptDelivery(businessId, workflow.Id, triggerKey))
                            continue;

                        candidates.Add(BuildCandidate(businessId, workflow, lead, null));
                    }

                    continue;
                }

                foreach (var order in orders.Where(item => item.IsPaid && item.PaidUtc is not null))
                {
                    if (order.PaidUtc < workflow.EligibleFromUtc || !HasPromotionalConsent(businessId, order.Email)) continue;
                    var dueAt = order.PaidUtc!.Value.Add(BuildDelay(workflow.DelayAmount, workflow.DelayUnit));
                    if (dueAt > now)
                        continue;

                    var triggerKey = order.OrderNumber;
                    if (!CanAttemptDelivery(businessId, workflow.Id, triggerKey))
                        continue;

                    candidates.Add(BuildCandidate(businessId, workflow, null, order));
                }
            }

            return candidates;
        }
    }

    public IReadOnlyList<ParfaitAutomationDispatchCandidate> GetDueDispatchCandidatesForAllBusinesses()
    {
        var businessIds = _db.CommerceBusinesses.AsNoTracking()
            .Where(business => business.IsActive && business.Status == "Active"
                && (_db.CommerceBusinessSettings.Any(settings => settings.CommerceBusinessId == business.Id && settings.CustomerAutomationStateJson != null)
                    || _db.MarketingDestinationDeliveries.Any(delivery => delivery.CommerceBusinessId == business.Id
                        && delivery.Provider == EmailProvider && delivery.Channel == EmailChannel
                        && delivery.CanonicalSource == ShipmentSource && (delivery.Status == "pending" || delivery.Status == "retry"))))
            .Select(business => business.Id)
            .ToList();

        var candidates = new List<ParfaitAutomationDispatchCandidate>();
        foreach (var businessId in businessIds)
        {
            candidates.AddRange(GetDueDispatchCandidates(businessId));
        }
        return candidates;
    }

    private ParfaitAutomationDispatchCandidate BuildCandidate(
        Guid businessId,
        ParfaitAutomationWorkflowRecord workflow,
        ParfaitAutomationCartLeadRecord? lead,
        ParfaitOrderRecord? order)
    {
        var context = BuildTemplateContext(businessId, workflow, lead, order);
        return new ParfaitAutomationDispatchCandidate
        {
            CommerceBusinessId = businessId,
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            WorkflowUpdatedTicks = workflow.UpdatedUtc.Ticks,
            TriggerType = workflow.TriggerType,
            TriggerKey = order?.OrderNumber ?? $"{lead!.Id:N}:{lead.LastActivityUtc.Ticks}",
            ToEmail = context.Email,
            FirstName = context.FirstName,
            Subject = ApplyTemplate(workflow.Subject, context),
            HtmlBody = BuildEmailHtml(workflow, context),
            OrderNumber = order?.OrderNumber,
            CheckoutAttemptId = lead?.CheckoutAttemptId
        };
    }

    private TemplateContext BuildTemplateContext(
        Guid businessId,
        ParfaitAutomationWorkflowRecord workflow,
        ParfaitAutomationCartLeadRecord? lead,
        ParfaitOrderRecord? order)
    {
        var baseUrl = ResolvePublicBaseUrl(businessId);
        var firstName = CleanOptional(order?.FirstName) ?? CleanOptional(lead?.FirstName) ?? "there";
        var email = NormalizeEmail(order?.Email ?? lead?.Email);
        var discountCode = ResolveDiscountCode(businessId, workflow.DiscountCode);
        var items = order?.Items ?? lead?.Items ?? [];
        var triggerLabel = workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart ? "Cart Ready" : "Order Follow Up";
        var ctaUrl = ApplyTemplate(string.IsNullOrWhiteSpace(workflow.CtaUrl)
            ? (workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart ? "/store/checkout" : "/store")
            : workflow.CtaUrl, new Dictionary<string, string>())
            .Trim();

        if (ctaUrl.StartsWith("/", StringComparison.Ordinal))
        {
            ctaUrl = ctaUrl == "/store" ? baseUrl
                : ctaUrl.StartsWith("/store/", StringComparison.Ordinal) ? baseUrl + ctaUrl[6..]
                : new Uri(new Uri(baseUrl), ctaUrl).ToString();
        }
        else if (!ctaUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !ctaUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ctaUrl = baseUrl.TrimEnd('/') + "/" + ctaUrl.TrimStart('/');
        }

        if (!Uri.TryCreate(ctaUrl, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Customer email links must use a secure published address.");
        return new TemplateContext
        {
            Email = email,
            FirstName = firstName,
            OrderNumber = order?.OrderNumber ?? "",
            ProductNames = SummarizeProductNames(items),
            CartTotalLabel = Money(lead?.TotalCents ?? order?.TotalCents ?? 0),
            DiscountCode = discountCode,
            CtaUrl = ctaUrl,
            TriggerLabel = triggerLabel,
            StoreName = ResolveStoreName(businessId),
            PostalAddress = LoadStoreUnsafe(businessId).EmailPostalAddress,
            UnsubscribeUrl = UnsubscribeUrl(businessId, email, baseUrl)
        };
    }

    private List<ParfaitAutomationDiscountOptionViewModel> BuildDiscountOptions() =>
        BuildDiscountOptions(_products.GetDefaultBusinessId());

    private List<ParfaitAutomationDiscountOptionViewModel> BuildDiscountOptions(Guid businessId)
    {
        var options = new List<ParfaitAutomationDiscountOptionViewModel>();
        var settings = _products.GetCommerceSettings(businessId);
        if (settings.HasActiveGlobalDiscount)
        {
            options.Add(new ParfaitAutomationDiscountOptionViewModel
            {
                Code = settings.GlobalDiscount.Code,
                Label = $"{settings.GlobalDiscount.Code} · Global {settings.GlobalDiscount.SummaryLabel}"
            });
        }

        options.AddRange(_products.GetAllProducts(businessId)
            .SelectMany(product => product.DiscountCodes
                .Where(code => code.IsActive && !string.IsNullOrWhiteSpace(code.Code) && code.Amount > 0)
                .Select(code => new ParfaitAutomationDiscountOptionViewModel
                {
                    Code = code.Code,
                    Label = $"{code.Code} · {product.Name} {code.SummaryLabel}"
                })));

        return options
            .GroupBy(option => option.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Dictionary<Guid, int> BuildDueCountLookup(ParfaitAutomationStoreRecord store, DateTime utcNow, Guid businessId)
    {
        return GetDueDispatchCandidates(businessId).GroupBy(candidate => candidate.WorkflowId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private List<ParfaitAutomationCustomerViewModel> BuildCustomers(ParfaitAutomationStoreRecord store, Guid businessId)
    {
        var ordersByEmail = _orders.GetAllOrders(businessId)
            .Where(order => !string.IsNullOrWhiteSpace(order.Email))
            .GroupBy(order => NormalizeEmail(order.Email), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(order => order.CreatedUtc).ToList(), StringComparer.OrdinalIgnoreCase);

        var leadsByEmail = store.CartLeads
            .Where(lead => !lead.IsConverted && !string.IsNullOrWhiteSpace(lead.Email))
            .GroupBy(lead => NormalizeEmail(lead.Email), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(lead => lead.LastActivityUtc).First(), StringComparer.OrdinalIgnoreCase);

        var allEmails = ordersByEmail.Keys
            .Concat(leadsByEmail.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var customers = new List<ParfaitAutomationCustomerViewModel>();
        foreach (var email in allEmails)
        {
            ordersByEmail.TryGetValue(email, out var customerOrders);
            leadsByEmail.TryGetValue(email, out var lead);

            var firstOrder = customerOrders?.FirstOrDefault();
            var paidOrders = customerOrders?
                .Where(order => order.IsPaid || order.IsRefundedPayment)
                .ToList()
                ?? [];

            var firstName = CleanOptional(firstOrder?.FirstName)
                ?? CleanOptional(lead?.FirstName)
                ?? email.Split('@')[0];

            customers.Add(new ParfaitAutomationCustomerViewModel
            {
                Email = email,
                FirstName = firstName,
                LastName = CleanOptional(firstOrder?.LastName) ?? CleanOptional(lead?.LastName) ?? "",
                Phone = CleanOptional(firstOrder?.Phone) ?? CleanOptional(lead?.Phone) ?? "",
                OrderCount = paidOrders.Count,
                LifetimeSpendCents = paidOrders.Sum(order => order.NetRevenueCents),
                LastPurchaseUtc = paidOrders.FirstOrDefault()?.PaidUtc ?? paidOrders.FirstOrDefault()?.CreatedUtc,
                HasOpenCart = lead is not null,
                OpenCartValueCents = lead?.TotalCents ?? 0,
                OpenCartItemCount = lead?.Items.Sum(item => item.Quantity) ?? 0,
                LastCartActivityUtc = lead?.LastActivityUtc,
                Tone = lead is not null ? "warning" : paidOrders.Count > 0 ? "success" : "info",
                CartItems = lead?.Items ?? [],
                Orders = (customerOrders ?? [])
                    .Take(6)
                    .Select(order => new ParfaitAutomationCustomerOrderViewModel
                    {
                        OrderNumber = order.OrderNumber,
                        CreatedUtc = order.CreatedUtc,
                        PaymentStatus = order.PaymentStatus,
                        TotalCents = order.TotalCents,
                        ItemCount = order.Items.Sum(item => item.Quantity)
                    })
                    .ToList()
            });
        }

        return customers
            .OrderByDescending(customer => customer.HasOpenCart)
            .ThenByDescending(customer => customer.LastPurchaseUtc ?? customer.LastCartActivityUtc ?? DateTime.MinValue)
            .ToList();
    }

    private string BuildEmailHtml(ParfaitAutomationWorkflowRecord workflow, TemplateContext context)
    {
        var enc = HtmlEncoder.Default;
        var subject = enc.Encode(ApplyTemplate(workflow.Subject, context));
        var headline = enc.Encode(ApplyTemplate(workflow.Headline, context));
        var body = enc.Encode(ApplyTemplate(workflow.Body, context)).Replace("\n", "<br/>");
        var ctaLabel = enc.Encode(ApplyTemplate(string.IsNullOrWhiteSpace(workflow.CtaLabel) ? "Open Store" : workflow.CtaLabel, context));
        var ctaUrl = enc.Encode(context.CtaUrl);
        var storeName = enc.Encode(context.StoreName);
        var discountCode = enc.Encode(context.DiscountCode);
        var triggerLabel = enc.Encode(context.TriggerLabel);
        var orderNumber = enc.Encode(context.OrderNumber);

        return
$"""
<div style="font-family:Inter,Arial,sans-serif;line-height:1.7;color:#22130f;background:#f8f1ea;padding:28px;">
  <div style="max-width:620px;margin:0 auto;background:#fffaf6;border:1px solid #76503e;border-radius:26px;overflow:hidden;box-shadow:0 20px 34px rgba(34,19,15,0.12);">
    <div style="padding:18px 24px;background:linear-gradient(135deg,#7a4f3b 0%,#2b1c16 100%);color:#fffaf5;">
      <div style="font-size:12px;font-weight:800;letter-spacing:0.18em;text-transform:uppercase;">{storeName}</div>
      <div style="margin-top:10px;font-size:28px;font-weight:900;letter-spacing:0.08em;text-transform:uppercase;">{headline}</div>
    </div>
    <div style="padding:24px;">
      <div style="display:inline-flex;align-items:center;padding:7px 12px;border-radius:999px;border:1px solid rgba(43,28,22,0.18);background:#f6ede6;color:#6b4634;font-size:12px;font-weight:800;letter-spacing:0.12em;text-transform:uppercase;">{triggerLabel}</div>
      <p style="margin:16px 0 0;color:#412821;font-size:15px;">{body}</p>
      <div style="margin-top:16px;padding:16px 18px;border-radius:20px;background:#fff;border:1px solid rgba(118,80,62,0.22);">
        <div style="font-size:13px;color:#6c4e40;font-weight:800;letter-spacing:0.14em;text-transform:uppercase;">Subject</div>
        <div style="margin-top:6px;color:#211511;font-size:16px;font-weight:700;">{subject}</div>
        {(string.IsNullOrWhiteSpace(orderNumber) ? "" : $"<div style=\"margin-top:10px;color:#6c4e40;font-size:14px;\">Order {orderNumber}</div>")}
        {(string.IsNullOrWhiteSpace(discountCode) ? "" : $"<div style=\"margin-top:10px;color:#1f6b51;font-size:14px;font-weight:700;\">Offer code: {discountCode}</div>")}
      </div>
      <div style="margin-top:22px;">
        <a href="{ctaUrl}" style="display:inline-block;padding:14px 22px;border-radius:999px;background:linear-gradient(145deg,#6f4b3b 0%,#2b1c16 100%);border:1px solid rgba(43,28,22,0.96);color:#fffaf5;text-decoration:none;font-size:13px;font-weight:900;letter-spacing:0.14em;text-transform:uppercase;">{ctaLabel}</a>
      </div>
      <p style="margin-top:28px;font-size:12px;color:#412821;">{storeName}<br/>{enc.Encode(context.PostalAddress)}<br/>
        <a href="{enc.Encode(context.UnsubscribeUrl)}">Unsubscribe from promotional emails</a></p>
    </div>
  </div>
</div>
""";
    }

    private static ParfaitAutomationWorkflowRecord NormalizeWorkflow(ParfaitAutomationWorkflowEditorInput input)
    {
        var now = DateTime.UtcNow;
        var defaults = new ParfaitAutomationWorkflowEditorInput();
        return new ParfaitAutomationWorkflowRecord
        {
            Id = input.Id.GetValueOrDefault() == Guid.Empty ? Guid.NewGuid() : input.Id!.Value,
            Name = CleanRequired(input.Name, defaults.Name),
            TriggerType = ParfaitAutomationTriggerTypes.Normalize(input.TriggerType),
            DelayAmount = Math.Clamp(input.DelayAmount, 1, 30),
            DelayUnit = ParfaitAutomationDelayUnits.Normalize(input.DelayUnit),
            Subject = CleanRequired(input.Subject, defaults.Subject),
            Headline = CleanRequired(input.Headline, defaults.Headline),
            Body = CleanRequired(input.Body, defaults.Body),
            CtaLabel = CleanRequired(input.CtaLabel, defaults.CtaLabel),
            CtaUrl = CleanRequired(input.CtaUrl, defaults.CtaUrl),
            DiscountCode = CleanOptional(input.DiscountCode) ?? "",
            IsActive = input.IsActive,
            UpdatedUtc = now,
            CreatedUtc = now
        };
    }

    private void CleanupUnsafe(ParfaitAutomationStoreRecord store, DateTime utcNow)
    {
        store.CartLeads = store.CartLeads
            .Where(lead => lead.LastActivityUtc >= utcNow.Subtract(CartLeadRetentionWindow) || lead.IsConverted)
            .ToList();
    }

    private ParfaitAutomationStoreRecord LoadStoreUnsafe(Guid businessId)
    {
        if (businessId == Guid.Empty) throw new ArgumentException("A commerce business is required.", nameof(businessId));
        var json = _db.CommerceBusinessSettings.AsNoTracking()
            .Where(x => x.CommerceBusinessId == businessId).Select(x => x.CustomerAutomationStateJson).SingleOrDefault();
        var store = json is null ? new ParfaitAutomationStoreRecord()
            : JsonSerializer.Deserialize<ParfaitAutomationStoreRecord>(json)
                ?? throw new InvalidOperationException("The store automation configuration could not be read.");
        store.PersistenceJson = json;
        store.Workflows ??= [];
        store.CartLeads ??= [];
        store.Dispatches = ReadDeliveryActivity(businessId);
        return store;
    }

    private void SaveStoreUnsafe(ParfaitAutomationStoreRecord store, Guid businessId)
    {
        CleanupUnsafe(store, DateTime.UtcNow);
        store.UpdatedUtc = DateTime.UtcNow;
        var settings = _db.CommerceBusinessSettings.SingleOrDefault(x => x.CommerceBusinessId == businessId);
        if (settings is null)
        {
            settings = new Domain.Entities.CommerceBusinessSettings { CommerceBusinessId = businessId };
            _db.CommerceBusinessSettings.Add(settings);
        }
        else
        {
            _db.Entry(settings).Reload();
            if (settings.CustomerAutomationStateJson != store.PersistenceJson)
                throw new InvalidOperationException("Automation settings changed. Reload and try again.");
        }
        settings.CustomerAutomationStateJson = JsonSerializer.Serialize(store);
        _db.SaveChanges();
        store.PersistenceJson = settings.CustomerAutomationStateJson;
    }

    private string ResolvePublicBaseUrl(Guid businessId) => TryResolvePublicBaseUrl(businessId)
        ?? throw new InvalidOperationException("Publish and verify this store's public address before enabling customer emails.");

    private string? TryResolvePublicBaseUrl(Guid businessId)
    {
        var canonical = _stores?.ResolveCanonicalPublicRootAsync(businessId).GetAwaiter().GetResult();
        if (!Uri.TryCreate(canonical, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString().TrimEnd('/');
    }

    private string ResolveStoreName(Guid businessId) =>
        _db?.CommerceBusinesses.AsNoTracking()
            .Where(business => business.Id == businessId)
            .Select(business => business.DisplayName)
            .SingleOrDefault()
        ?? (businessId == _products.GetDefaultBusinessId() ? "Parfait" : "Store");

    private string ResolveDiscountCode(Guid businessId, string? requestedCode)
    {
        var normalized = ParfaitProductCatalogDefaults.NormalizeDiscountCode(requestedCode);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        return BuildDiscountOptions(businessId).Any(option => option.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : "";
    }

    private static string SummarizeProductNames(IReadOnlyList<ParfaitValidatedCartItem> items)
    {
        var names = items
            .Select(item => CleanRequired(item.Name, "Parfait item"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (names.Count == 0)
            return "your Parfait favorites";
        if (names.Count == 1)
            return names[0];
        if (names.Count == 2)
            return $"{names[0]} and {names[1]}";
        return $"{names[0]}, {names[1]}, and {names[2]}";
    }

    private static TimeSpan BuildDelay(int amount, string unit)
    {
        var safeAmount = Math.Clamp(amount, 1, 30);
        return string.Equals(unit, ParfaitAutomationDelayUnits.Hours, StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromHours(safeAmount)
            : TimeSpan.FromDays(safeAmount);
    }

    private static string BuildDelayLabel(int amount, string unit)
    {
        var safeAmount = Math.Clamp(amount, 1, 30);
        var normalizedUnit = ParfaitAutomationDelayUnits.Normalize(unit);
        var labelUnit = normalizedUnit == ParfaitAutomationDelayUnits.Hours
            ? (safeAmount == 1 ? "hour" : "hours")
            : (safeAmount == 1 ? "day" : "days");
        return $"{safeAmount} {labelUnit} later";
    }

    private static string DispatchTone(string status) => status switch
    {
        "Failed" or "Rejected" or "Uncertain" => "danger",
        "Pending" or "Queued" or "Retry" or "Sending" => "warning",
        "Superseded" => "info",
        _ => "success"
    };

    private static string Money(int cents) => "$" + (cents / 100m).ToString("0.00");

    private static string NormalizeEmail(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string CleanRequired(string? value, string fallback)
    {
        var cleaned = CleanOptional(value);
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string? CleanOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ApplyTemplate(string template, TemplateContext context)
    {
        return ApplyTemplate(template, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["first_name"] = context.FirstName,
            ["order_number"] = context.OrderNumber,
            ["product_names"] = context.ProductNames,
            ["discount_code"] = context.DiscountCode,
            ["cart_total"] = context.CartTotalLabel,
            ["cta_url"] = context.CtaUrl
        });
    }

    private static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var output = template ?? string.Empty;
        foreach (var pair in values)
        {
            output = output.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return output;
    }

    private sealed class TemplateContext
    {
        public string PostalAddress { get; init; } = "";
        public string UnsubscribeUrl { get; init; } = "";
        public string Email { get; init; } = "";
        public string FirstName { get; init; } = "Parfait";
        public string OrderNumber { get; init; } = "";
        public string ProductNames { get; init; } = "your Parfait favorites";
        public string CartTotalLabel { get; init; } = "$0.00";
        public string DiscountCode { get; init; } = "";
        public string CtaUrl { get; init; } = "https://shopparfait.com/store";
        public string TriggerLabel { get; init; } = "Store";
        public string StoreName { get; init; } = "Store";
    }
}
