using System.Security.Cryptography;
using System.Text;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ParfaitApp.Services;

namespace Legend.Commerce;

/// <summary>
/// Fail-closed startup verification of the exact originally reconciled
/// commerce media/automation/team manifest. A configured cutover may not
/// accept live checkout requests unless the target bytes match the receipt.
/// Read only; does not migrate, publish, or perform any provider operation.
/// </summary>
public sealed class CommerceCutoverStartupProof(IServiceScopeFactory scopes, IConfiguration configuration)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Infrastructure.WebsiteRuntime.CommerceSharedHostCutoverGate.IsConfigured(configuration))
            return;

        if (!Guid.TryParse(configuration["Commerce:SharedHostCutover:BusinessId"], out var id))
            throw new InvalidOperationException("Commerce cutover business identity unavailable.");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<ParfaitStoragePaths>();
        var business = await db.CommerceBusinesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.IsActive && x.Status == "Active",
                cancellationToken);
        if (business is null)
            throw new InvalidOperationException("Commerce cutover business is not active.");

        var actual = await new CommerceLegacyTransfer(db)
            .ReadManifestAsync(id, business.Key, storage, cancellationToken);
        var expected = configuration["Commerce:SharedHostCutover:ReconciledManifestSha256"]
            ?? throw new InvalidOperationException("Commerce cutover manifest proof missing.");

        var actualBytes = Encoding.ASCII.GetBytes(actual);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        if (actualBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            throw new InvalidOperationException("Commerce cutover storage does not match the reconciled manifest.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
