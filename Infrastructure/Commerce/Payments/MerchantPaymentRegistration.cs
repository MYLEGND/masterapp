using Infrastructure.Commerce.Payments.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.Commerce.Payments;

public static class MerchantPaymentRegistration
{
    public static IServiceCollection AddMerchantPayments(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddDataProtection();
        services.TryAddScoped<MerchantConnectionService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMerchantPaymentGateway,SquareMerchantPaymentGateway>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMerchantPaymentGateway,StripeMerchantPaymentGateway>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMerchantPaymentGateway,PayPalMerchantPaymentGateway>());
        return services;
    }
}
