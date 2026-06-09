namespace Shared.Analytics;

public sealed record WebsiteLeadSignalRules(
    bool LeadReadyRequiresContactStep,
    bool LeadReadyRequiresValidPhone,
    bool LeadReadyRequiresValidEmail,
    bool QualifiedLeadRequiresLeadReady,
    int QualifiedLeadMinimumTotalScore)
{
    public static WebsiteLeadSignalRules Default { get; } = new(
        LeadReadyRequiresContactStep: true,
        LeadReadyRequiresValidPhone: true,
        LeadReadyRequiresValidEmail: false,
        QualifiedLeadRequiresLeadReady: true,
        QualifiedLeadMinimumTotalScore: 80);
}

public sealed record WebsiteLeadSignalInput(
    bool FunnelStartObserved,
    bool ContactStepReached,
    bool ContactInputStarted,
    bool RequiredContactFieldsCompleted,
    bool SubmitAttempted,
    bool ConfirmedWebsiteLead,
    string? Phone,
    string? Email,
    int TotalSignalScore);

public sealed record WebsiteLeadSignalEvaluation(
    bool LeadFormStarted,
    bool ContactIntentCaptured,
    bool LeadReady,
    bool QualifiedLead,
    bool MetaOptimizationLead,
    bool ConfirmedWebsiteLead);

public static class WebsiteLeadSignalClassifier
{
    public static WebsiteLeadSignalEvaluation Evaluate(
        WebsiteLeadSignalInput input,
        WebsiteLeadSignalRules? rules = null)
    {
        rules ??= WebsiteLeadSignalRules.Default;

        var validPhone = HasValidPhone(input.Phone);
        var validEmail = HasValidEmail(input.Email);
        var leadFormStarted = input.FunnelStartObserved;
        var contactIntentCaptured = input.ContactStepReached || input.ContactInputStarted || input.SubmitAttempted;

        var leadReady = input.RequiredContactFieldsCompleted &&
                        (!rules.LeadReadyRequiresContactStep || input.ContactStepReached) &&
                        (!rules.LeadReadyRequiresValidPhone || validPhone) &&
                        (!rules.LeadReadyRequiresValidEmail || validEmail);

        var qualifiedLead = input.ConfirmedWebsiteLead &&
                            input.TotalSignalScore >= rules.QualifiedLeadMinimumTotalScore &&
                            (!rules.QualifiedLeadRequiresLeadReady || leadReady);

        return new WebsiteLeadSignalEvaluation(
            LeadFormStarted: leadFormStarted,
            ContactIntentCaptured: contactIntentCaptured,
            LeadReady: leadReady,
            QualifiedLead: qualifiedLead,
            MetaOptimizationLead: qualifiedLead,
            ConfirmedWebsiteLead: input.ConfirmedWebsiteLead);
    }

    public static bool HasValidPhone(string? phone) =>
        !string.IsNullOrWhiteSpace(phone) && phone.Count(char.IsDigit) >= 10;

    public static bool HasValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Contains('@', StringComparison.Ordinal);
}
