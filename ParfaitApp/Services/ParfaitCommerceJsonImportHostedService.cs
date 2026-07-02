using Microsoft.Extensions.Options;

namespace ParfaitApp.Services;

public sealed class ParfaitCommerceJsonImportOptions
{
    public bool RunOnStartup { get; set; }
}

public sealed class ParfaitCommerceJsonImportHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ParfaitCommerceJsonImportHostedService> _logger;
    private readonly IOptions<ParfaitCommerceJsonImportOptions> _options;

    public ParfaitCommerceJsonImportHostedService(
        IServiceProvider services,
        ILogger<ParfaitCommerceJsonImportHostedService> logger,
        IOptions<ParfaitCommerceJsonImportOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.RunOnStartup)
            return;

        using var scope = _services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ParfaitCommerceJsonImportService>();

        var report = await importer.ImportAsync(cancellationToken);

        _logger.LogInformation(
            "Parfait commerce JSON import completed. JsonProducts={JsonProducts}, DbProducts={DbProducts}, JsonOrders={JsonOrders}, DbOrders={DbOrders}, DbOrderLines={DbOrderLines}, DbImages={DbImages}, DbInventoryItems={DbInventoryItems}, DbDiscounts={DbDiscounts}",
            report.JsonProducts,
            report.DbProducts,
            report.JsonOrders,
            report.DbOrders,
            report.DbOrderLines,
            report.DbImages,
            report.DbInventoryItems,
            report.DbDiscounts);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
