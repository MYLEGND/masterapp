using Domain.FinancialIntelligence;
using Infrastructure.FinancialIntelligence.Rules;
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
        services.AddScoped<IFinancialIntelligenceRule, RecurringChargeReviewRule>();
        services.AddScoped<IFinancialIntelligenceRule, StaleFinancialDataRule>();
        services.AddScoped<IFinancialIntelligenceRule, CashFlowShortfallRule>();
        services.AddScoped<IFinancialIntelligenceEvaluationService, FinancialIntelligenceEvaluationService>();

        return services;
    }
}
