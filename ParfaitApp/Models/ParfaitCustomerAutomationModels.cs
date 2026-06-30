using System.ComponentModel.DataAnnotations;

namespace ParfaitApp.Models;

public static class ParfaitAutomationTriggerTypes
{
    public const string PostPurchase = "post-purchase";
    public const string AbandonedCart = "abandoned-cart";

    public static string ToLabel(string triggerType) => Normalize(triggerType) switch
    {
        PostPurchase => "Post Purchase",
        AbandonedCart => "Abandoned Cart",
        _ => "Workflow"
    };

    public static string Normalize(string? triggerType)
    {
        var cleaned = triggerType?.Trim().ToLowerInvariant();
        return cleaned switch
        {
            PostPurchase => PostPurchase,
            AbandonedCart => AbandonedCart,
            _ => PostPurchase
        };
    }
}

public static class ParfaitAutomationDelayUnits
{
    public const string Hours = "Hours";
    public const string Days = "Days";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Hours, StringComparison.OrdinalIgnoreCase)
            ? Hours
            : Days;
    }
}

public sealed class ParfaitAutomationWorkflowRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Customer Flow";
    public string TriggerType { get; set; } = ParfaitAutomationTriggerTypes.PostPurchase;
    public int DelayAmount { get; set; } = 2;
    public string DelayUnit { get; set; } = ParfaitAutomationDelayUnits.Days;
    public string Subject { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Body { get; set; } = "";
    public string CtaLabel { get; set; } = "";
    public string CtaUrl { get; set; } = "";
    public string DiscountCode { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ParfaitAutomationCartLeadRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CheckoutAttemptId { get; set; } = "";
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Phone { get; set; } = "";
    public List<ParfaitValidatedCartItem> Items { get; set; } = [];
    public int SubtotalCents { get; set; }
    public int DiscountCents { get; set; }
    public int ShippingCents { get; set; }
    public int TaxCents { get; set; }
    public int TotalCents { get; set; }
    public string DiscountCode { get; set; } = "";
    public DateTime FirstCapturedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    public bool IsConverted { get; set; }
    public string? ConvertedOrderNumber { get; set; }
}

public sealed class ParfaitAutomationDispatchLogRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public string TriggerKey { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public string RecipientFirstName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Status { get; set; } = "Sent";
    public string ErrorMessage { get; set; } = "";
    public string? OrderNumber { get; set; }
    public string? CheckoutAttemptId { get; set; }
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ParfaitAutomationStoreRecord
{
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<ParfaitAutomationWorkflowRecord> Workflows { get; set; } = [];
    public List<ParfaitAutomationCartLeadRecord> CartLeads { get; set; } = [];
    public List<ParfaitAutomationDispatchLogRecord> Dispatches { get; set; } = [];
}

public sealed class ParfaitAutomationCheckoutLeadCaptureRequest
{
    public string? CheckoutAttemptId { get; set; }
    public string? DiscountCode { get; set; }
    public ParfaitCheckoutCustomerRequest Customer { get; set; } = new();
    public List<ParfaitCheckoutItemRequest> Items { get; set; } = [];
}

public sealed class ParfaitAutomationDiscountOptionViewModel
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class ParfaitAutomationWorkflowCardViewModel
{
    public required Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string TriggerType { get; init; } = ParfaitAutomationTriggerTypes.PostPurchase;
    public string TriggerLabel { get; init; } = "Post Purchase";
    public int DelayAmount { get; init; }
    public string DelayUnit { get; init; } = ParfaitAutomationDelayUnits.Days;
    public string DelayLabel { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Body { get; init; } = "";
    public string CtaLabel { get; init; } = "";
    public string CtaUrl { get; init; } = "";
    public string DiscountCode { get; init; } = "";
    public bool IsActive { get; init; }
    public int DueNowCount { get; init; }
    public int SentLast7Days { get; init; }
    public DateTime? LastSentUtc { get; init; }
    public string Tone { get; init; } = "info";
}

public sealed class ParfaitAutomationCustomerOrderViewModel
{
    public string OrderNumber { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public string PaymentStatus { get; init; } = "";
    public int TotalCents { get; init; }
    public int ItemCount { get; init; }
}

public sealed class ParfaitAutomationCustomerViewModel
{
    public string Email { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Phone { get; init; } = "";
    public int OrderCount { get; init; }
    public int LifetimeSpendCents { get; init; }
    public DateTime? LastPurchaseUtc { get; init; }
    public bool HasOpenCart { get; init; }
    public int OpenCartValueCents { get; init; }
    public int OpenCartItemCount { get; init; }
    public DateTime? LastCartActivityUtc { get; init; }
    public string Tone { get; init; } = "info";
    public IReadOnlyList<ParfaitValidatedCartItem> CartItems { get; init; } = [];
    public IReadOnlyList<ParfaitAutomationCustomerOrderViewModel> Orders { get; init; } = [];
}

public sealed class ParfaitAutomationDispatchActivityViewModel
{
    public string WorkflowName { get; init; } = "";
    public string TriggerLabel { get; init; } = "";
    public string RecipientLabel { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Status { get; init; } = "Sent";
    public string Tone { get; init; } = "success";
    public string Detail { get; init; } = "";
    public DateTime OccurredUtc { get; init; }
}

public sealed class ParfaitAutomationWorkflowEditorInput
{
    public Guid? Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = "Customer Flow";

    [Required]
    public string TriggerType { get; set; } = ParfaitAutomationTriggerTypes.PostPurchase;

    [Range(1, 30)]
    public int DelayAmount { get; set; } = 2;

    [Required]
    public string DelayUnit { get; set; } = ParfaitAutomationDelayUnits.Days;

    [Required]
    [StringLength(140)]
    public string Subject { get; set; } = "Hey {{first_name}}, Parfait is still with you";

    [Required]
    [StringLength(140)]
    public string Headline { get; set; } = "Stay locked into Parfait.";

    [Required]
    [StringLength(2400)]
    public string Body { get; set; } = "We wanted to follow up and keep your Parfait momentum moving. {{product_names}} still looks strong on you.";

    [StringLength(60)]
    public string CtaLabel { get; set; } = "Open Parfait";

    [StringLength(240)]
    public string CtaUrl { get; set; } = "/store";

    [StringLength(60)]
    public string DiscountCode { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

public sealed class ParfaitAutomationWorkspaceViewModel
{
    public List<ParfaitAutomationWorkflowCardViewModel> Workflows { get; init; } = [];
    public List<ParfaitAutomationCustomerViewModel> Customers { get; init; } = [];
    public List<ParfaitAutomationDispatchActivityViewModel> Activity { get; init; } = [];
    public List<ParfaitAutomationDiscountOptionViewModel> DiscountOptions { get; init; } = [];
    public ParfaitAutomationWorkflowEditorInput NewWorkflow { get; init; } = new();
    public int ActiveWorkflowCount { get; init; }
    public int AudienceCount { get; init; }
    public int OpenCartLeadCount { get; init; }
    public int SentLast7Days { get; init; }
}

public sealed class ParfaitAutomationDispatchCandidate
{
    public required Guid WorkflowId { get; init; }
    public required string WorkflowName { get; init; }
    public required string TriggerType { get; init; }
    public required string TriggerKey { get; init; }
    public required string ToEmail { get; init; }
    public required string FirstName { get; init; }
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }
    public string? OrderNumber { get; init; }
    public string? CheckoutAttemptId { get; init; }
}
