using Domain.FinancialIntelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.FinancialIntelligence;

public static class FinancialIntelligenceServiceCollectionExtensions
{
    public static IServiceCollection AddMasterAppFinancialIntelligence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IFinancialConnectionService, FinancialConnectionService>();
        services.AddScoped<IFinancialImportService, FinancialImportService>();
        services.AddScoped<IRecurringFinancialStreamService, RecurringFinancialStreamService>();
        services.AddScoped<IExpenseLensSynchronizationService, ExpenseLensSynchronizationService>();

        return services;
    }
}
