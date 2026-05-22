using System;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ProtectWebsite.Services.Booking;

public sealed record PublicBookingContext(
    Guid WebsiteLeadId,
    string? AgentSlug,
    string? QuoteType,
    string? PageKey,
    DateTime IssuedUtc);

public interface IPublicBookingContextProtector
{
    string Protect(PublicBookingContext context);
    bool TryUnprotect(string? token, out PublicBookingContext? context);
}

public sealed class PublicBookingContextProtector : IPublicBookingContextProtector
{
    private static readonly TimeSpan MaxTokenAge = TimeSpan.FromDays(7);
    private readonly IDataProtector _protector;

    public PublicBookingContextProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("ProtectWebsite.PublicBookingContext.v1");
    }

    public string Protect(PublicBookingContext context)
    {
        return _protector.Protect(JsonSerializer.Serialize(context));
    }

    public bool TryUnprotect(string? token, out PublicBookingContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(token.Trim());
            context = JsonSerializer.Deserialize<PublicBookingContext>(payload);
            if (context == null)
            {
                return false;
            }

            var age = DateTime.UtcNow - context.IssuedUtc;
            if (age < TimeSpan.FromMinutes(-15) || age > MaxTokenAge)
            {
                context = null;
                return false;
            }

            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }
}
