using System.Text.Json;
using Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Bookings;

public sealed record BusinessBookingTicket(
    Guid CommerceBusinessId,
    Guid AgentProfileId,
    string AgentUserId,
    string ActorDisplay,
    string TimeZoneId,
    DateTime ExpiresUtc);

public sealed class BusinessBookingTicketProtector : IDisposable
{
    private const string Purpose = "LEGEND.BusinessBooking.Ticket.v1";
    private const string SharedApplicationName = "LEGEND.BusinessBooking";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector;
    private readonly ServiceProvider? ownedProvider;

    public BusinessBookingTicketProtector(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector(Purpose);
    }

    private BusinessBookingTicketProtector(IDataProtectionProvider provider, ServiceProvider ownedProvider)
    {
        protector = provider.CreateProtector(Purpose);
        this.ownedProvider = ownedProvider;
    }

    public static BusinessBookingTicketProtector CreateShared(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var services = new ServiceCollection();
        var sharedDevelopmentKeys = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "..",
            "AgentPortal",
            "App_Data",
            "business-booking-keys"));

        services.AddPlatformDataProtection(
            configuration,
            environment,
            SharedApplicationName,
            sharedDevelopmentKeys);

        var provider = services.BuildServiceProvider();
        return new BusinessBookingTicketProtector(
            provider.GetRequiredService<IDataProtectionProvider>(),
            provider);
    }

    public string Protect(BusinessBookingTicket ticket)
        => protector.Protect(JsonSerializer.Serialize(ticket, JsonOptions));

    public BusinessBookingTicket? TryUnprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var json = protector.Unprotect(token);
            var ticket = JsonSerializer.Deserialize<BusinessBookingTicket>(json, JsonOptions);
            if (ticket is null ||
                ticket.ExpiresUtc <= DateTime.UtcNow ||
                ticket.CommerceBusinessId == Guid.Empty ||
                ticket.AgentProfileId == Guid.Empty ||
                string.IsNullOrWhiteSpace(ticket.AgentUserId) ||
                string.IsNullOrWhiteSpace(ticket.ActorDisplay))
                return null;
            return ticket;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => ownedProvider?.Dispose();
}
