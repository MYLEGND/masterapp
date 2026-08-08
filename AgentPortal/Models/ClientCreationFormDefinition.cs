using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentPortal.Models;

/// <summary>
/// The single field contract for AgentPortal client creation. The Razor form
/// consumes its choices and the native app requests this contract at runtime.
/// Creation itself remains in <see cref="Controllers.ClientsController.Create"/>.
/// </summary>
public sealed record ClientCreationFormDefinition(
    string Title,
    string Detail,
    IReadOnlyList<ClientCreationFormSectionDefinition> Sections,
    IReadOnlyList<ClientCreationFormActionDefinition> Actions)
{
    public ClientCreationFormFieldDefinition Field(string key) => Sections
        .SelectMany(section => section.Fields)
        .Single(field => string.Equals(field.Key, key, StringComparison.Ordinal));

    public static ClientCreationFormDefinition Create(
        CreateClientViewModel model,
        bool canSetFounderSubscriptionOptions)
    {
        var portalRecordTypes = new[] { "Client", "BusinessClient" };
        var householdStatuses = new[] { "Married", "Domestic Partnership" };
        var portalCondition = new ClientCreationFieldCondition("RecordType", portalRecordTypes);
        var householdCondition = new ClientCreationFieldCondition("MaritalStatus", householdStatuses);
        var customSubscriptionCondition = new ClientCreationFieldCondition(
            "SubscriptionPriceType", new[] { "Custom" });
        var selectedAnchorCondition = new ClientCreationFieldCondition(
            "SubscriptionBillingAnchorMode", new[] { "SpecificDayOfMonth" });

        var sections = new List<ClientCreationFormSectionDefinition>
        {
            new(
                "record-type",
                "Record Type",
                "Choose the workspace type first so required fields and downstream portal behavior stay aligned.",
                Array.Empty<ClientCreationFieldCondition>(),
                new[]
                {
                    Choice(
                        "RecordType",
                        "Record Type",
                        model.RecordType,
                        required: true,
                        options: new[]
                        {
                            Option("Lead", "Lead"),
                            Option("Client", "Client"),
                            Option("BusinessClient", "Business Client")
                        })
                }),
            new(
                "account-management",
                "Account Management",
                "Required for Client and Business Client records. The client can change this later from their profile.",
                new[] { portalCondition },
                new[]
                {
                    Choice(
                        "AccountManagementMode",
                        "Account Management",
                        model.AccountManagementMode,
                        requiredWhen: new[] { portalCondition },
                        options: new[]
                        {
                            Option("SharedAccount", "Shared Account"),
                            Option("SelfManaged", "Self Managed")
                        })
                }),
            new(
                "client-details",
                "Client Details",
                "Capture the primary contact information and household status used across the CRM and client workspace.",
                Array.Empty<ClientCreationFieldCondition>(),
                new[]
                {
                    Text("FirstName", "First Name", model.FirstName, "text", requiredWhen: new[] { portalCondition }, autocomplete: "given-name"),
                    Text("LastName", "Last Name", model.LastName, "text", requiredWhen: new[] { portalCondition }, autocomplete: "family-name"),
                    Text("Email", "Email", model.Email, "email", requiredWhen: new[] { portalCondition }, autocomplete: "email"),
                    Text("Phone", "Phone", model.Phone, "phone", autocomplete: "tel"),
                    Text("DOB", "Date of Birth", Date(model.DOB), "date"),
                    Choice(
                        "MaritalStatus",
                        "Marital Status",
                        model.MaritalStatus,
                        options: new[]
                        {
                            Option("", "Select status"),
                            Option("Single", "Single"),
                            Option("Married", "Married"),
                            Option("Domestic Partnership", "Domestic Partnership"),
                            Option("Divorced", "Divorced"),
                            Option("Widowed", "Widowed")
                        })
                }),
            new(
                "significant-other",
                "Significant Other",
                "Required for married and domestic-partnership households so the client record opens with the right family context.",
                new[] { portalCondition, householdCondition },
                new[]
                {
                    Text("SignificantOtherFirstName", "First Name", model.SignificantOtherFirstName, "text", requiredWhen: new[] { portalCondition, householdCondition }, autocomplete: "given-name"),
                    Text("SignificantOtherLastName", "Last Name", model.SignificantOtherLastName, "text", requiredWhen: new[] { portalCondition, householdCondition }, autocomplete: "family-name"),
                    Text("SignificantOtherDOB", "Date of Birth", Date(model.SignificantOtherDOB), "date", requiredWhen: new[] { portalCondition, householdCondition }),
                    Text("SignificantOtherPhone", "Phone", model.SignificantOtherPhone, "phone", autocomplete: "tel"),
                    Text("SignificantOtherEmail", "Email", model.SignificantOtherEmail, "email", autocomplete: "email")
                }),
            new(
                "subscription",
                "Client Subscription",
                "Portal clients can leave intake with an agent-scoped Client Portal subscription offer and activation invitation already prepared.",
                new[] { portalCondition },
                SubscriptionFields(model, canSetFounderSubscriptionOptions, portalCondition, customSubscriptionCondition, selectedAnchorCondition)),
            new(
                "crm",
                "CRM",
                "Seed relationship tracking details now if this record should land in a specific pipeline state the moment it is created.",
                Array.Empty<ClientCreationFieldCondition>(),
                new[]
                {
                    Choice(
                        "CrmStatus",
                        "CRM Status",
                        model.CrmStatus,
                        options: new[]
                        {
                            Option("Lead", "Lead"),
                            Option("Prospect", "Prospect"),
                            Option("Active", "Active Client"),
                            Option("Dormant", "Inactive")
                        },
                        valueRules: new[]
                        {
                            Rule("Active", portalCondition)
                        }),
                    Choice(
                        "PipelineStage",
                        "Pipeline Stage",
                        model.PipelineStage,
                        options: new[]
                        {
                            Option("NewLead", "Lead"),
                            Option("Opportunities", "Opportunities"),
                            Option("Contacted", "Contacted"),
                            Option("Qualified", "Qualified"),
                            Option("Client", "Clients"),
                            Option("BusinessClient", "Business Clients"),
                            Option("MeetingScheduled", "Meeting Scheduled"),
                            Option("ProposalSent", "Proposal Sent"),
                            Option("ApplicationStarted", "Application Started"),
                            Option("Submitted", "Submitted"),
                            Option("ClosedLost", "Not Moving Forward"),
                            Option("Nurture", "Nurture")
                        },
                        valueRules: new[]
                        {
                            Rule("NewLead", new ClientCreationFieldCondition("RecordType", new[] { "Lead" })),
                            Rule("Client", new ClientCreationFieldCondition("RecordType", new[] { "Client" })),
                            Rule("BusinessClient", new ClientCreationFieldCondition("RecordType", new[] { "BusinessClient" }))
                        }),
                    Choice(
                        "CrmPriority",
                        "Priority",
                        model.CrmPriority,
                        options: new[]
                        {
                            Option("Low", "Low"),
                            Option("Normal", "Normal"),
                            Option("High", "High"),
                            Option("Urgent", "Urgent")
                        }),
                    Text("CrmLastTouch", "Last Touch", Date(model.CrmLastTouch), "date"),
                    Text("CrmNextDate", "Next Action Date", Date(model.CrmNextDate), "date"),
                    Text("CrmNextText", "Next Action", model.CrmNextText, "text", placeholder: "What will you do?"),
                    Text("CrmTags", "Tags", model.CrmTags, "text", placeholder: "Comma separated"),
                    Text("CrmNotes", "Relationship Notes", model.CrmNotes, "multiline", placeholder: "Add private relationship context")
                })
        };

        return new ClientCreationFormDefinition(
            "Create Client",
            "Complete client intake without leaving LEGEND. The same AgentPortal workflow validates, provisions, and saves this record.",
            sections,
            new[]
            {
                new ClientCreationFormActionDefinition("today-last-touch", "Today for Last Touch", "CrmLastTouch", "today"),
                new ClientCreationFormActionDefinition("today-next-action", "Today for Next Action", "CrmNextDate", "today"),
                new ClientCreationFormActionDefinition("clear-crm", "Clear CRM", null, "clear-crm")
            });
    }

    private static IReadOnlyList<ClientCreationFormFieldDefinition> SubscriptionFields(
        CreateClientViewModel model,
        bool canSetFounderSubscriptionOptions,
        ClientCreationFieldCondition portalCondition,
        ClientCreationFieldCondition customSubscriptionCondition,
        ClientCreationFieldCondition selectedAnchorCondition)
    {
        var anchorOptions = new List<ClientCreationFormOptionDefinition>
        {
            Option("FirstOfMonth", "1st of month"),
            Option("FifteenthOfMonth", "15th of month")
        };
        if (canSetFounderSubscriptionOptions)
            anchorOptions.Add(Option("SpecificDayOfMonth", "Agent-selected day"));

        var fields = new List<ClientCreationFormFieldDefinition>
        {
            Choice(
                "SubscriptionPriceType",
                "Monthly Tier",
                model.SubscriptionPriceType,
                requiredWhen: new[] { portalCondition },
                options: new[]
                {
                    Option("", "Select a subscription"),
                    Option("Fixed50", "$50 / month"),
                    Option("Fixed75", "$75 / month"),
                    Option("Fixed100", "$100 / month"),
                    Option("Fixed150", "$150 / month"),
                    Option("Custom", "Custom")
                }),
            Text(
                "SubscriptionCustomMonthlyAmount",
                "Custom Monthly Amount (USD)",
                model.SubscriptionCustomMonthlyAmount?.ToString("0.00"),
                "number",
                requiredWhen: new[] { portalCondition, customSubscriptionCondition },
                visibleWhen: new[] { portalCondition, customSubscriptionCondition },
                placeholder: canSetFounderSubscriptionOptions ? "0.00" : "50.00",
                minimum: canSetFounderSubscriptionOptions ? 0 : 50,
                maximum: 2500,
                step: 0.01m),
            Choice(
                "SubscriptionBillingAnchorMode",
                "Billing Anchor",
                model.SubscriptionBillingAnchorMode,
                options: anchorOptions),
            Hidden("SubscriptionCurrency", model.SubscriptionCurrency)
        };

        if (canSetFounderSubscriptionOptions)
        {
            fields.Add(Text(
                "SubscriptionBillingAnchorDay",
                "Agent-Selected Day",
                model.SubscriptionBillingAnchorDay?.ToString(),
                "number",
                requiredWhen: new[] { portalCondition, selectedAnchorCondition },
                visibleWhen: new[] { portalCondition, selectedAnchorCondition },
                placeholder: "15",
                minimum: 1,
                maximum: 31,
                step: 1));
        }

        return fields;
    }

    private static ClientCreationFormFieldDefinition Choice(
        string key,
        string label,
        string? value,
        bool required = false,
        IReadOnlyList<ClientCreationFormOptionDefinition>? options = null,
        IReadOnlyList<ClientCreationFieldCondition>? visibleWhen = null,
        IReadOnlyList<ClientCreationFieldCondition>? requiredWhen = null,
        IReadOnlyList<ClientCreationFormValueRule>? valueRules = null)
        => new(
            key, label, "choice", value ?? string.Empty, required,
            options ?? Array.Empty<ClientCreationFormOptionDefinition>(),
            visibleWhen ?? Array.Empty<ClientCreationFieldCondition>(),
            requiredWhen ?? Array.Empty<ClientCreationFieldCondition>(),
            valueRules ?? Array.Empty<ClientCreationFormValueRule>(),
            null, null, null, null, null, null);

    private static ClientCreationFormFieldDefinition Text(
        string key,
        string label,
        string? value,
        string inputKind,
        bool required = false,
        IReadOnlyList<ClientCreationFieldCondition>? visibleWhen = null,
        IReadOnlyList<ClientCreationFieldCondition>? requiredWhen = null,
        string? placeholder = null,
        string? autocomplete = null,
        decimal? minimum = null,
        decimal? maximum = null,
        decimal? step = null)
        => new(
            key, label, inputKind, value ?? string.Empty, required,
            Array.Empty<ClientCreationFormOptionDefinition>(),
            visibleWhen ?? Array.Empty<ClientCreationFieldCondition>(),
            requiredWhen ?? Array.Empty<ClientCreationFieldCondition>(),
            Array.Empty<ClientCreationFormValueRule>(),
            placeholder, autocomplete, minimum, maximum, step, null);

    private static ClientCreationFormFieldDefinition Hidden(string key, string? value)
        => new(
            key, key, "hidden", value ?? string.Empty, false,
            Array.Empty<ClientCreationFormOptionDefinition>(),
            Array.Empty<ClientCreationFieldCondition>(),
            Array.Empty<ClientCreationFieldCondition>(),
            Array.Empty<ClientCreationFormValueRule>(),
            null, null, null, null, null, null);

    private static ClientCreationFormOptionDefinition Option(string value, string label) => new(value, label);

    private static ClientCreationFormValueRule Rule(string value, params ClientCreationFieldCondition[] conditions)
        => new(value, conditions);

    private static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd");
}

public sealed record ClientCreationFormSectionDefinition(
    string Key,
    string Title,
    string Detail,
    IReadOnlyList<ClientCreationFieldCondition> VisibleWhen,
    IReadOnlyList<ClientCreationFormFieldDefinition> Fields);

public sealed record ClientCreationFormFieldDefinition(
    string Key,
    string Label,
    string InputKind,
    string DefaultValue,
    bool Required,
    IReadOnlyList<ClientCreationFormOptionDefinition> Options,
    IReadOnlyList<ClientCreationFieldCondition> VisibleWhen,
    IReadOnlyList<ClientCreationFieldCondition> RequiredWhen,
    IReadOnlyList<ClientCreationFormValueRule> ValueRules,
    string? Placeholder,
    string? Autocomplete,
    decimal? Minimum,
    decimal? Maximum,
    decimal? Step,
    string? HelpText);

public sealed record ClientCreationFormOptionDefinition(string Value, string Label);

public sealed record ClientCreationFieldCondition(string Field, IReadOnlyList<string> EqualsAny);

public sealed record ClientCreationFormValueRule(
    string Value,
    IReadOnlyList<ClientCreationFieldCondition> Conditions);

public sealed record ClientCreationFormActionDefinition(
    string Key,
    string Label,
    string? Field,
    string Value);
