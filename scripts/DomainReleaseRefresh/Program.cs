using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var hostname = WebsiteDomainService.NormalizeHostname(Required("LEGEND_RELEASE_DOMAIN"));
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["WebsiteDomains:CloudflareZoneId"] = Required("LEGEND_RELEASE_ZONE_ID"),
    ["WebsiteDomains:ApiToken"] = Required("LEGEND_RELEASE_DOMAIN_TOKEN"),
    ["WebsiteDomains:CnameTarget"] = Required("LEGEND_RELEASE_CNAME_TARGET")
}).Build();
var options = new DbContextOptionsBuilder<MasterAppDbContext>()
    .UseSqlServer(Required("LEGEND_RELEASE_DB_CONNECTION"))
    .Options;
await using var db = new MasterAppDbContext(options);
var binding = await db.Set<WebsiteDomainBinding>().AsNoTracking()
    .SingleAsync(row => row.Hostname == hostname);
var domains = new WebsiteDomainService(db, new DomainHttpClientFactory(), configuration);

for (var attempt = 1; attempt <= 8; attempt++)
{
    var refreshed = await domains.RefreshAsync(binding.CommerceBusinessId, binding.Id);
    Console.WriteLine($"Domain verification attempt {attempt}: status={refreshed.Status}, certificate={refreshed.CertificateStatus}");
    if (refreshed.Status == "active" && refreshed.CertificateStatus == "active")
        return;
    if (attempt < 8) await Task.Delay(TimeSpan.FromSeconds(5));
}

throw new InvalidOperationException("The shared domain authority did not verify the custom hostname after routing cutover.");

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing release input {name}.");

sealed class DomainHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
