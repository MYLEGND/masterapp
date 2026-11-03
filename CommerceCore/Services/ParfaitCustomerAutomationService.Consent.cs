using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed partial class ParfaitCustomerAutomationService
{
    public const string MarketingConsentText = "Email me product news, offers, and cart reminders from this store. I can unsubscribe at any time.";
    private const string ConsentSource = "commerce-marketing-opt-in-v1";

    public void RecordMarketingOptIn(Guid businessId, ParfaitOrderRecord order, bool isInternal)
    {
        if (order.CommerceBusinessId != businessId) throw new InvalidOperationException("Consent must belong to the order's store.");
        using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? _db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable) : null;
        var source = "checkout:" + order.CommerceOrderId.ToString("N");
        // Retrying the same checkout must not undo an unsubscribe.
        if (_db.WebsiteLeads.Any(row => row.CommerceBusinessId == businessId && row.SourceCtaKey == ConsentSource && row.SourcePageKey == source)) return;
        _db.WebsiteLeads.Add(new WebsiteLead {
            LeadId = Guid.NewGuid(), CommerceBusinessId = businessId, FirstName = order.FirstName,
            LastName = order.LastName, Email = NormalizeEmail(order.Email),
            SourcePageKey = source, SourceCtaKey = ConsentSource, InterestType = "StoreEmailSubscription",
            MarketingEmailConsent = true, TermsAccepted = false, CallTextConsent = false,
            IsInternal = isInternal, Environment = isInternal ? "Internal" : "Production",
            CreatedUtc = DateTime.UtcNow, Status = "Subscribed",
            MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string> {
                ["consentText"] = MarketingConsentText,
                ["unsubscribeToken"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            })
        });
        _db.SaveChanges();
        transaction?.Commit();
    }

    private WebsiteLead? LatestMarketingConsent(Guid businessId, string email)
    {
        var normalized = NormalizeEmail(email);
        return _db.WebsiteLeads.AsNoTracking().Where(row => row.CommerceBusinessId == businessId
            && row.SourceCtaKey == ConsentSource && row.Email.ToLower() == normalized)
            .OrderByDescending(row => row.CreatedUtc).ThenByDescending(row => row.Id).FirstOrDefault();
    }

    private static string? ConsentToken(WebsiteLead lead)
    {
        if (string.IsNullOrWhiteSpace(lead.MetadataJson)) return null;
        using var metadata = JsonDocument.Parse(lead.MetadataJson);
        return metadata.RootElement.TryGetProperty("unsubscribeToken", out var token) && token.ValueKind == JsonValueKind.String
            ? token.GetString() : null;
    }

    private string UnsubscribeUrl(Guid businessId, string email, string publicRoot)
    {
        var lead = LatestMarketingConsent(businessId, email)
            ?? throw new InvalidOperationException("The email subscription is unavailable.");
        var token = ConsentToken(lead);
        if (token?.Length != 64) throw new InvalidOperationException("The email preference link is unavailable.");
        return new Uri(new Uri(publicRoot), "/store/email-preferences").ToString()
            + $"?businessId={businessId:D}&leadId={lead.LeadId:D}&token={Uri.EscapeDataString(token)}";
    }

    public CommerceEmailPreferencesViewModel? GetEmailPreferences(Guid businessId, Guid leadId, string token)
    {
        var lead = FindConsentForToken(businessId, leadId, token);
        if (lead is null) return null;
        var name = _db.CommerceBusinesses.AsNoTracking().Where(row => row.Id == businessId).Select(row => row.DisplayName).SingleOrDefault();
        return name is null ? null : new(businessId, leadId, token, name, LatestMarketingConsent(businessId, lead.Email)?.MarketingEmailConsent != true);
    }

    public bool Unsubscribe(Guid businessId, Guid leadId, string token)
    {
        using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? _db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable) : null;
        var lead = FindConsentForToken(businessId, leadId, token);
        if (lead is null) return false;
        var email = NormalizeEmail(lead.Email);
        foreach (var record in _db.WebsiteLeads.Where(row => row.CommerceBusinessId == businessId
            && row.SourceCtaKey == ConsentSource && row.Email.ToLower() == email))
        {
            record.MarketingEmailConsent = false;
            record.Status = "Unsubscribed";
        }
        _db.SaveChanges();
        transaction?.Commit();
        return true;
    }

    private WebsiteLead? FindConsentForToken(Guid businessId, Guid leadId, string token)
    {
        if (token?.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        var lead = _db.WebsiteLeads.AsNoTracking().SingleOrDefault(row => row.CommerceBusinessId == businessId
            && row.LeadId == leadId && row.SourceCtaKey == ConsentSource);
        if (lead is null) return null;
        try
        {
            var saved = ConsentToken(lead);
            return saved is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(saved), Encoding.UTF8.GetBytes(token)) ? lead : null;
        }
        catch (JsonException)
        {
            _logger.LogWarning("Invalid commerce email preference evidence. Business:{BusinessId} Lead:{LeadId}", businessId, lead.Id);
            return null;
        }
    }

    public void SaveEmailSettings(Guid businessId, CommerceAutomationEmailSettingsInput input)
    {
        lock (StateLock)
        {
            var store = LoadStoreUnsafe(businessId);
            if (store.PersistenceJson is not null && input.ExpectedUpdatedTicks != store.UpdatedUtc.Ticks)
                throw new InvalidOperationException("Email settings changed. Reload and review the latest details.");
            var address = input.PostalAddress?.Trim() ?? "";
            if (address.Length > 800) throw new InvalidOperationException("The mailing address is too long.");
            store.EmailPostalAddress = address;
            SaveStoreUnsafe(store, businessId);
        }
    }
}
