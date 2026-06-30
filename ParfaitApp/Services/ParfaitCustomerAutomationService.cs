using System.Text.Encodings.Web;
using System.Text.Json;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitCustomerAutomationService
{
    private static readonly TimeSpan CartLeadRetentionWindow = TimeSpan.FromDays(45);
    private static readonly TimeSpan DispatchRetentionWindow = TimeSpan.FromDays(180);

    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ParfaitOrderService _orders;
    private readonly ParfaitProductService _products;
    private readonly object _lock = new();

    public ParfaitCustomerAutomationService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ParfaitOrderService orders,
        ParfaitProductService products)
    {
        _environment = environment;
        _configuration = configuration;
        _orders = orders;
        _products = products;
    }

    private string DataPath => Path.Combine(_environment.ContentRootPath, "App_Data", "parfait-customer-automations.json");

    public ParfaitAutomationWorkspaceViewModel GetWorkspaceViewModel()
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
            CleanupUnsafe(store, DateTime.UtcNow);

            var workflowDueCounts = BuildDueCountLookup(store, DateTime.UtcNow);
            var sentLast7Days = store.Dispatches.Count(dispatch =>
                string.Equals(dispatch.Status, "Sent", StringComparison.OrdinalIgnoreCase)
                && dispatch.OccurredUtc >= DateTime.UtcNow.AddDays(-7));

            var customers = BuildCustomers(store);
            var activity = store.Dispatches
                .OrderByDescending(dispatch => dispatch.OccurredUtc)
                .Take(16)
                .Select(dispatch => new ParfaitAutomationDispatchActivityViewModel
                {
                    WorkflowName = dispatch.WorkflowName,
                    TriggerLabel = ParfaitAutomationTriggerTypes.ToLabel(dispatch.TriggerType),
                    RecipientLabel = string.IsNullOrWhiteSpace(dispatch.RecipientFirstName)
                        ? dispatch.RecipientEmail
                        : $"{dispatch.RecipientFirstName} · {dispatch.RecipientEmail}",
                    Subject = dispatch.Subject,
                    Status = dispatch.Status,
                    Tone = DispatchTone(dispatch.Status),
                    Detail = string.IsNullOrWhiteSpace(dispatch.ErrorMessage)
                        ? (string.IsNullOrWhiteSpace(dispatch.OrderNumber)
                            ? "Delivered through Parfait automation."
                            : $"Order {dispatch.OrderNumber}")
                        : dispatch.ErrorMessage,
                    OccurredUtc = dispatch.OccurredUtc
                })
                .ToList();

            return new ParfaitAutomationWorkspaceViewModel
            {
                Workflows = store.Workflows
                    .OrderBy(workflow => workflow.TriggerType)
                    .ThenBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(workflow => new ParfaitAutomationWorkflowCardViewModel
                    {
                        Id = workflow.Id,
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
                        SentLast7Days = store.Dispatches.Count(dispatch =>
                            dispatch.WorkflowId == workflow.Id &&
                            string.Equals(dispatch.Status, "Sent", StringComparison.OrdinalIgnoreCase) &&
                            dispatch.OccurredUtc >= DateTime.UtcNow.AddDays(-7)),
                        LastSentUtc = store.Dispatches
                            .Where(dispatch =>
                                dispatch.WorkflowId == workflow.Id &&
                                string.Equals(dispatch.Status, "Sent", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(dispatch => dispatch.OccurredUtc)
                            .Select(dispatch => (DateTime?)dispatch.OccurredUtc)
                            .FirstOrDefault(),
                        Tone = workflow.IsActive
                            ? workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart ? "warning" : "success"
                            : "muted"
                    })
                    .ToList(),
                Customers = customers,
                Activity = activity,
                DiscountOptions = BuildDiscountOptions(),
                NewWorkflow = CreateDefaultWorkflow(),
                ActiveWorkflowCount = store.Workflows.Count(workflow => workflow.IsActive),
                AudienceCount = customers.Count,
                OpenCartLeadCount = store.CartLeads.Count(lead => !lead.IsConverted),
                SentLast7Days = sentLast7Days
            };
        }
    }

    public void SaveWorkflow(ParfaitAutomationWorkflowEditorInput input)
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
            CleanupUnsafe(store, DateTime.UtcNow);

            var workflow = NormalizeWorkflow(input);
            var existingIndex = store.Workflows.FindIndex(item => item.Id == workflow.Id);
            if (existingIndex >= 0)
            {
                workflow.CreatedUtc = store.Workflows[existingIndex].CreatedUtc;
                store.Workflows[existingIndex] = workflow;
            }
            else
            {
                store.Workflows.Add(workflow);
            }

            SaveStoreUnsafe(store);
        }
    }

    public void DeleteWorkflow(Guid id)
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
            store.Workflows.RemoveAll(workflow => workflow.Id == id);
            SaveStoreUnsafe(store);
        }
    }

    public void CaptureCheckoutLead(ParfaitAutomationCheckoutLeadCaptureRequest request, ParfaitCartQuoteResponse quote)
    {
        var checkoutAttemptId = CleanOptional(request.CheckoutAttemptId);
        var email = NormalizeEmail(request.Customer.Email);
        if (string.IsNullOrWhiteSpace(checkoutAttemptId) || string.IsNullOrWhiteSpace(email))
            return;

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

        lock (_lock)
        {
            var store = LoadStoreUnsafe();
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

            SaveStoreUnsafe(store);
        }
    }

    public void MarkOrderConverted(ParfaitOrderRecord order)
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
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

            SaveStoreUnsafe(store);
        }
    }

    public IReadOnlyList<ParfaitAutomationDispatchCandidate> GetDueDispatchCandidates()
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
            CleanupUnsafe(store, DateTime.UtcNow);

            var now = DateTime.UtcNow;
            var orders = _orders.GetAllOrders();
            var candidates = new List<ParfaitAutomationDispatchCandidate>();

            foreach (var workflow in store.Workflows.Where(item => item.IsActive))
            {
                if (workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart)
                {
                    foreach (var lead in store.CartLeads.Where(item => !item.IsConverted))
                    {
                        if (string.IsNullOrWhiteSpace(lead.Email) || lead.Items.Count == 0)
                            continue;

                        var dueAt = lead.LastActivityUtc.Add(BuildDelay(workflow.DelayAmount, workflow.DelayUnit));
                        if (dueAt > now)
                            continue;

                        var triggerKey = $"{lead.Id:N}:{lead.LastActivityUtc.Ticks}";
                        if (HasSuccessfulDispatch(store, workflow.Id, triggerKey))
                            continue;

                        candidates.Add(BuildCandidate(workflow, lead, null));
                    }

                    continue;
                }

                foreach (var order in orders.Where(item => item.IsPaid && item.PaidUtc is not null))
                {
                    var dueAt = order.PaidUtc!.Value.Add(BuildDelay(workflow.DelayAmount, workflow.DelayUnit));
                    if (dueAt > now)
                        continue;

                    var triggerKey = order.OrderNumber;
                    if (HasSuccessfulDispatch(store, workflow.Id, triggerKey))
                        continue;

                    candidates.Add(BuildCandidate(workflow, null, order));
                }
            }

            return candidates;
        }
    }

    public void MarkDispatchSent(ParfaitAutomationDispatchCandidate candidate)
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
            store.Dispatches.Add(new ParfaitAutomationDispatchLogRecord
            {
                WorkflowId = candidate.WorkflowId,
                WorkflowName = candidate.WorkflowName,
                TriggerType = candidate.TriggerType,
                TriggerKey = candidate.TriggerKey,
                RecipientEmail = candidate.ToEmail,
                RecipientFirstName = candidate.FirstName,
                Subject = candidate.Subject,
                Status = "Sent",
                OrderNumber = candidate.OrderNumber,
                CheckoutAttemptId = candidate.CheckoutAttemptId,
                OccurredUtc = DateTime.UtcNow
            });
            SaveStoreUnsafe(store);
        }
    }

    public void MarkDispatchFailed(ParfaitAutomationDispatchCandidate candidate, string errorMessage)
    {
        lock (_lock)
        {
            var store = LoadStoreUnsafe();
            store.Dispatches.Add(new ParfaitAutomationDispatchLogRecord
            {
                WorkflowId = candidate.WorkflowId,
                WorkflowName = candidate.WorkflowName,
                TriggerType = candidate.TriggerType,
                TriggerKey = candidate.TriggerKey,
                RecipientEmail = candidate.ToEmail,
                RecipientFirstName = candidate.FirstName,
                Subject = candidate.Subject,
                Status = "Failed",
                ErrorMessage = CleanOptional(errorMessage) ?? "Delivery failed.",
                OrderNumber = candidate.OrderNumber,
                CheckoutAttemptId = candidate.CheckoutAttemptId,
                OccurredUtc = DateTime.UtcNow
            });
            SaveStoreUnsafe(store);
        }
    }

    private ParfaitAutomationDispatchCandidate BuildCandidate(
        ParfaitAutomationWorkflowRecord workflow,
        ParfaitAutomationCartLeadRecord? lead,
        ParfaitOrderRecord? order)
    {
        var context = BuildTemplateContext(workflow, lead, order);
        return new ParfaitAutomationDispatchCandidate
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
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
        ParfaitAutomationWorkflowRecord workflow,
        ParfaitAutomationCartLeadRecord? lead,
        ParfaitOrderRecord? order)
    {
        var baseUrl = ResolvePublicBaseUrl();
        var firstName = CleanOptional(order?.FirstName) ?? CleanOptional(lead?.FirstName) ?? "Parfait";
        var email = NormalizeEmail(order?.Email ?? lead?.Email);
        var discountCode = ResolveDiscountCode(workflow.DiscountCode);
        var items = order?.Items ?? lead?.Items ?? [];
        var triggerLabel = workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart ? "Cart Ready" : "Order Follow Up";
        var ctaUrl = ApplyTemplate(string.IsNullOrWhiteSpace(workflow.CtaUrl)
            ? (workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart ? "/store/checkout" : "/store")
            : workflow.CtaUrl, new Dictionary<string, string>())
            .Trim();

        if (ctaUrl.StartsWith("/", StringComparison.Ordinal))
        {
            ctaUrl = baseUrl.TrimEnd('/') + ctaUrl;
        }
        else if (!ctaUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !ctaUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ctaUrl = baseUrl.TrimEnd('/') + "/" + ctaUrl.TrimStart('/');
        }

        return new TemplateContext
        {
            Email = email,
            FirstName = firstName,
            OrderNumber = order?.OrderNumber ?? "",
            ProductNames = SummarizeProductNames(items),
            CartTotalLabel = Money(lead?.TotalCents ?? order?.TotalCents ?? 0),
            DiscountCode = discountCode,
            CtaUrl = ctaUrl,
            TriggerLabel = triggerLabel
        };
    }

    private List<ParfaitAutomationDiscountOptionViewModel> BuildDiscountOptions()
    {
        var options = new List<ParfaitAutomationDiscountOptionViewModel>();
        var settings = _products.GetCommerceSettings();
        if (settings.HasActiveGlobalDiscount)
        {
            options.Add(new ParfaitAutomationDiscountOptionViewModel
            {
                Code = settings.GlobalDiscount.Code,
                Label = $"{settings.GlobalDiscount.Code} · Global {settings.GlobalDiscount.SummaryLabel}"
            });
        }

        options.AddRange(_products.GetAllProducts()
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

    private Dictionary<Guid, int> BuildDueCountLookup(ParfaitAutomationStoreRecord store, DateTime utcNow)
    {
        var lookup = store.Workflows.ToDictionary(workflow => workflow.Id, _ => 0);
        var orders = _orders.GetAllOrders();

        foreach (var workflow in store.Workflows.Where(workflow => workflow.IsActive))
        {
            if (workflow.TriggerType == ParfaitAutomationTriggerTypes.AbandonedCart)
            {
                lookup[workflow.Id] = store.CartLeads.Count(lead =>
                    !lead.IsConverted &&
                    !string.IsNullOrWhiteSpace(lead.Email) &&
                    lead.Items.Count > 0 &&
                    lead.LastActivityUtc.Add(BuildDelay(workflow.DelayAmount, workflow.DelayUnit)) <= utcNow &&
                    !HasSuccessfulDispatch(store, workflow.Id, $"{lead.Id:N}:{lead.LastActivityUtc.Ticks}"));
                continue;
            }

            lookup[workflow.Id] = orders.Count(order =>
                order.IsPaid &&
                order.PaidUtc is not null &&
                order.PaidUtc.Value.Add(BuildDelay(workflow.DelayAmount, workflow.DelayUnit)) <= utcNow &&
                !HasSuccessfulDispatch(store, workflow.Id, order.OrderNumber));
        }

        return lookup;
    }

    private List<ParfaitAutomationCustomerViewModel> BuildCustomers(ParfaitAutomationStoreRecord store)
    {
        var ordersByEmail = _orders.GetAllOrders()
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
        var ctaLabel = enc.Encode(ApplyTemplate(string.IsNullOrWhiteSpace(workflow.CtaLabel) ? "Open Parfait" : workflow.CtaLabel, context));
        var ctaUrl = enc.Encode(context.CtaUrl);
        var storeName = enc.Encode((_configuration["Contact:WebsiteName"] ?? "Parfait").Trim());
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
    </div>
  </div>
</div>
""";
    }

    private ParfaitAutomationWorkflowEditorInput CreateDefaultWorkflow()
    {
        return new ParfaitAutomationWorkflowEditorInput
        {
            TriggerType = ParfaitAutomationTriggerTypes.PostPurchase,
            DelayAmount = 2,
            DelayUnit = ParfaitAutomationDelayUnits.Days,
            Subject = "Hey {{first_name}}, Parfait is still moving with you",
            Headline = "Stay locked into Parfait.",
            Body = "We wanted to follow up and keep your Parfait momentum moving. {{product_names}} still belongs in your rotation.",
            CtaLabel = "Open Parfait",
            CtaUrl = "/store",
            IsActive = true
        };
    }

    private static ParfaitAutomationWorkflowRecord NormalizeWorkflow(ParfaitAutomationWorkflowEditorInput input)
    {
        var now = DateTime.UtcNow;
        return new ParfaitAutomationWorkflowRecord
        {
            Id = input.Id.GetValueOrDefault() == Guid.Empty ? Guid.NewGuid() : input.Id!.Value,
            Name = CleanRequired(input.Name, "Customer Flow"),
            TriggerType = ParfaitAutomationTriggerTypes.Normalize(input.TriggerType),
            DelayAmount = Math.Clamp(input.DelayAmount, 1, 30),
            DelayUnit = ParfaitAutomationDelayUnits.Normalize(input.DelayUnit),
            Subject = CleanRequired(input.Subject, "Parfait Update"),
            Headline = CleanRequired(input.Headline, "Stay locked into Parfait."),
            Body = CleanRequired(input.Body, "Parfait is still moving with you."),
            CtaLabel = CleanRequired(input.CtaLabel, "Open Parfait"),
            CtaUrl = CleanRequired(input.CtaUrl, "/store"),
            DiscountCode = CleanOptional(input.DiscountCode) ?? "",
            IsActive = input.IsActive,
            UpdatedUtc = now,
            CreatedUtc = now
        };
    }

    private static bool HasSuccessfulDispatch(ParfaitAutomationStoreRecord store, Guid workflowId, string triggerKey)
    {
        return store.Dispatches.Any(dispatch =>
            dispatch.WorkflowId == workflowId &&
            dispatch.TriggerKey.Equals(triggerKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(dispatch.Status, "Sent", StringComparison.OrdinalIgnoreCase));
    }

    private void CleanupUnsafe(ParfaitAutomationStoreRecord store, DateTime utcNow)
    {
        store.CartLeads = store.CartLeads
            .Where(lead => lead.LastActivityUtc >= utcNow.Subtract(CartLeadRetentionWindow) || lead.IsConverted)
            .ToList();
        store.Dispatches = store.Dispatches
            .Where(dispatch => dispatch.OccurredUtc >= utcNow.Subtract(DispatchRetentionWindow))
            .OrderByDescending(dispatch => dispatch.OccurredUtc)
            .Take(500)
            .OrderBy(dispatch => dispatch.OccurredUtc)
            .ToList();
    }

    private ParfaitAutomationStoreRecord LoadStoreUnsafe()
    {
        EnsureDataFile();
        var json = File.ReadAllText(DataPath);
        var store = JsonSerializer.Deserialize<ParfaitAutomationStoreRecord>(json) ?? new ParfaitAutomationStoreRecord();
        store.Workflows ??= [];
        store.CartLeads ??= [];
        store.Dispatches ??= [];
        return store;
    }

    private void SaveStoreUnsafe(ParfaitAutomationStoreRecord store)
    {
        CleanupUnsafe(store, DateTime.UtcNow);
        store.UpdatedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllText(
            DataPath,
            JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void EnsureDataFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        if (!File.Exists(DataPath))
        {
            File.WriteAllText(
                DataPath,
                JsonSerializer.Serialize(new ParfaitAutomationStoreRecord(), new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private string ResolvePublicBaseUrl()
    {
        return CleanOptional(_configuration["Store:PublicBaseUrl"])
            ?? CleanOptional(_configuration["PublicSite:BaseUrl"])
            ?? "https://shopparfait.com";
    }

    private string ResolveDiscountCode(string? requestedCode)
    {
        var normalized = ParfaitProductCatalogDefaults.NormalizeDiscountCode(requestedCode);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        return BuildDiscountOptions().Any(option => option.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
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
        "Failed" => "danger",
        "Queued" => "warning",
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
        public string Email { get; init; } = "";
        public string FirstName { get; init; } = "Parfait";
        public string OrderNumber { get; init; } = "";
        public string ProductNames { get; init; } = "your Parfait favorites";
        public string CartTotalLabel { get; init; } = "$0.00";
        public string DiscountCode { get; init; } = "";
        public string CtaUrl { get; init; } = "https://shopparfait.com/store";
        public string TriggerLabel { get; init; } = "Parfait";
    }
}
