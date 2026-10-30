using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Domain.Entities;

namespace Infrastructure.Leads;

/// <summary>Snapshot the validated submission model before its transaction commits. Never derive an attestation from lead status.</summary>
public static class WebsiteSubmissionEvidence
{
    private const string DisclosurePurpose = "Legend.InsuranceForm.DisclosureEvidence.v1";
    private sealed record IssuedDisclosure(string Model, string Field, string Text);

    public static string ProtectAuthorization(IDataProtectionProvider protection, string model, string field, string text) =>
        protection.CreateProtector(DisclosurePurpose).Protect(JsonSerializer.Serialize(new IssuedDisclosure(model, field, text)));

    public static void Attach<T>(WebsiteLead lead, T submittedModel, string formType,
        string authorizationField, bool authorizationValue, string authorizationText, string? presentationJson = null,
        string? disclosureToken = null, IServiceProvider? services = null)
    {
        var disclosureSource = "Current server disclosure definition; evidence of the originally presented wording is unavailable";
        if (!string.IsNullOrWhiteSpace(disclosureToken))
        {
            if (disclosureToken.Length > 20000) throw new ArgumentException("Disclosure token exceeds its bound.");
            var protection = services?.GetService(typeof(IDataProtectionProvider)) as IDataProtectionProvider
                ?? throw new InvalidOperationException("The existing form disclosure protection service is unavailable.");
            var issued = JsonSerializer.Deserialize<IssuedDisclosure>(protection.CreateProtector(DisclosurePurpose).Unprotect(disclosureToken));
            if (issued is null || issued.Model != typeof(T).FullName || issued.Field != authorizationField)
                throw new InvalidOperationException("The disclosure evidence belongs to a different form or field.");
            authorizationText = issued.Text;
            disclosureSource = "Original server-issued form disclosure and submitted checkbox field; no proof of viewing or independent signature inferred";
        }
        JsonNode? presentation = null;
        if (!string.IsNullOrWhiteSpace(presentationJson))
        {
            if (presentationJson.Length > 250000) throw new ArgumentException("Submitted presentation exceeds its bound.");
            presentation = JsonNode.Parse(presentationJson, documentOptions: new JsonDocumentOptions { MaxDepth = 12 });
            if (presentation is not JsonObject || presentation["schemaVersion"]?.GetValue<int>() != 1 ||
                presentation["fields"] is not JsonArray fields || fields.Count > 1000)
                throw new ArgumentException("Invalid submitted presentation evidence.");
        }
        var root = string.IsNullOrWhiteSpace(lead.MetadataJson) ? new JsonObject() : JsonNode.Parse(lead.MetadataJson) as JsonObject ?? new JsonObject();
        root["SubmittedFormEvidence"] = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, formType, formKey = lead.SourcePageKey,
            formDefinitionVersion = typeof(T).Assembly.GetName().Version?.ToString(),
            capturedUtc = lead.CreatedUtc, source = "Validated server submission model",
            answers = submittedModel,
            presentationEvidence = presentation,
            presentationSource = presentation is null ? "Evidence unavailable" : "Submitted browser presentation; not independently attested",
            authorizations = new[] { new { field = authorizationField, text = authorizationText,
                disclosureVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorizationText))).ToLowerInvariant(),
                submittedState = authorizationValue, status = authorizationValue ? "Checked on submitted form" : "Not checked",
                timestampUtc = lead.CreatedUtc, source = disclosureSource, signatureEvidence = "No independent signature evidence captured" } },
            completeness = presentation is null
                ? "Validated answers and server disclosure wording captured. Browser presentation, question labels and conditional states unavailable."
                : "Validated answers and server disclosure wording captured separately from browser-reported labels, choices, repeated fields and presentation states. No independent signature or proof of viewing is inferred."
        });
        lead.MetadataJson = root.ToJsonString();
    }
}
