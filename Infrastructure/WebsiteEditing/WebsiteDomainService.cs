using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.WebsiteEditing;

/// <summary>Cloudflare for SaaS is the sole customer-domain and certificate authority.</summary>
public sealed class WebsiteDomainService(MasterAppDbContext db, IHttpClientFactory clients, IConfiguration configuration)
{
    public static string NormalizeHostname(string hostname)
    {
        var value = new IdnMapping().GetAscii((hostname ?? "").Trim().TrimEnd('.')).ToLowerInvariant();
        if (value.Length > 253 || !value.Contains('.') || Uri.CheckHostName(value) != UriHostNameType.Dns ||
            value.Split('.').Any(label => label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')) ||
            value == "mylegnd.com" || value.EndsWith(".mylegnd.com", StringComparison.Ordinal))
            throw new ArgumentException("Enter a business-owned domain, without a protocol or path.");
        return value;
    }

    public async Task<WebsiteDomainBinding> RegisterAsync(Guid businessId, string hostname, CancellationToken ct = default)
    {
        hostname = NormalizeHostname(hostname);
        if (!await db.CommerceBusinesses.AnyAsync(b => b.Id == businessId && b.IsActive && b.Status == "Active", ct))
            throw new InvalidOperationException("Business is unavailable.");
        var binding = await db.Set<WebsiteDomainBinding>().SingleOrDefaultAsync(x => x.Hostname == hostname, ct);
        if (binding is not null)
        {
            if (binding.CommerceBusinessId != businessId) throw new InvalidOperationException("Domain is already assigned.");
            return await RefreshAsync(businessId, binding.Id, ct);
        }
        // Reserve before the external call: unique hostname prevents cross-business races.
        EnsureConfigured();
        binding = new WebsiteDomainBinding { CommerceBusinessId = businessId, Hostname = hostname };
        db.Add(binding);
        await db.SaveChangesAsync(ct);
        return await RefreshAsync(businessId, binding.Id, ct);
    }

    public async Task<WebsiteDomainBinding> RefreshAsync(Guid businessId, Guid bindingId, CancellationToken ct = default)
    {
        var binding = await db.Set<WebsiteDomainBinding>().SingleAsync(x => x.Id == bindingId && x.CommerceBusinessId == businessId, ct);
        EnsureConfigured();
        JsonElement result;
        if (string.IsNullOrEmpty(binding.ProviderHostnameId))
        {
            // Recover a successful provider call whose receipt was not persisted, without creating a duplicate.
            var existing = await CallAsync(HttpMethod.Get, "?hostname=" + Uri.EscapeDataString(binding.Hostname), null, ct);
            var matches = existing.EnumerateArray().Where(x => x.GetProperty("hostname").GetString() == binding.Hostname).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous domain provider receipt.");
            result = matches.Length == 1 ? matches[0] : await CallAsync(HttpMethod.Post, "", new
            {
                hostname = binding.Hostname,
                ssl = new { method = "http", type = "dv" },
                custom_metadata = new { legend_binding_id = binding.Id.ToString("N"), legend_business_id = businessId.ToString("N") }
            }, ct);
        }
        else result = await CallAsync(HttpMethod.Get, "/" + Uri.EscapeDataString(binding.ProviderHostnameId), null, ct);

        // The editor promises a one-record onboarding flow: the customer points the
        // hostname at the LEGEND SaaS CNAME target and Cloudflare completes DCV.
        // Migrate older TXT-created pending hostnames and explicitly retrigger HTTP
        // DCV on user/background refresh so an already-correct CNAME can advance.
        if (ShouldRetryAutomaticHttpDcv(result))
        {
            var providerId = result.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Missing domain receipt.");
            result = await CallAsync(HttpMethod.Patch, "/" + Uri.EscapeDataString(providerId), new
            {
                ssl = new { method = "http", type = "dv" }
            }, ct);
        }

        ApplyReceipt(binding, result);
        if (binding.Status == "active" && !await RoutingConfirmedAsync(binding, ct)) binding.Status = "pending";
        binding.LastCheckedUtc = DateTime.UtcNow;
        binding.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return binding;
    }

    public async Task RemoveAsync(Guid businessId, Guid bindingId, CancellationToken ct = default)
    {
        var binding = await db.Set<WebsiteDomainBinding>().SingleAsync(x => x.Id == bindingId && x.CommerceBusinessId == businessId, ct);
        // Disable routing before provider removal; errors retain retryable local state.
        binding.Status = "removing";
        binding.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        if (binding.ProviderHostnameId.Length > 0)
            await CallAsync(HttpMethod.Delete, "/" + Uri.EscapeDataString(binding.ProviderHostnameId), null, ct);
        db.Remove(binding);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid?> ResolveAsync(string hostname, CancellationToken ct = default)
    {
        try { hostname = NormalizeHostname(hostname); } catch (ArgumentException) { return null; }
        var cutoff = DateTime.UtcNow.AddHours(-24);
        return await (from binding in db.Set<WebsiteDomainBinding>().AsNoTracking()
                      join business in db.CommerceBusinesses.AsNoTracking() on binding.CommerceBusinessId equals business.Id
                      where binding.Hostname == hostname && binding.Status == "active" && binding.CertificateStatus == "active" &&
                            binding.LastCheckedUtc >= cutoff && business.IsActive && business.Status == "Active"
                      select (Guid?)business.Id).SingleOrDefaultAsync(ct);
    }

    public static void ApplyReceipt(WebsiteDomainBinding binding, JsonElement result)
    {
        if (result.GetProperty("hostname").GetString() != binding.Hostname)
            throw new InvalidOperationException("Domain provider returned a different hostname.");
        if (!result.TryGetProperty("custom_metadata", out var metadata) ||
            !metadata.TryGetProperty("legend_binding_id", out var bindingId) || bindingId.GetString() != binding.Id.ToString("N") ||
            !metadata.TryGetProperty("legend_business_id", out var businessId) || businessId.GetString() != binding.CommerceBusinessId.ToString("N"))
            throw new InvalidOperationException("Domain provider ownership does not match this business.");
        binding.ProviderHostnameId = result.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing domain receipt.");
        binding.CertificateStatus = result.GetProperty("ssl").GetProperty("status").GetString() ?? "pending";
        var status = result.GetProperty("status").GetString() ?? "pending";
        binding.Status = status == "active" && binding.CertificateStatus == "active" ? "active" : "pending";
        var ssl = result.GetProperty("ssl");
        binding.VerificationJson = JsonSerializer.Serialize(new
        {
            providerStatus = status,
            certificateStatus = binding.CertificateStatus,
            validationMethod = ssl.TryGetProperty("method", out var method) ? method.GetString() : null,
            verificationErrors = result.TryGetProperty("verification_errors", out var verificationErrors) ? verificationErrors : (JsonElement?)null,
            certificateErrors = ssl.TryGetProperty("validation_errors", out var certificateErrors) ? certificateErrors : (JsonElement?)null,
            ownership = result.TryGetProperty("ownership_verification", out var ownership) ? ownership : (JsonElement?)null,
            certificate = ssl.TryGetProperty("validation_records", out var records) ? records : (JsonElement?)null
        });
    }

    private static bool ShouldRetryAutomaticHttpDcv(JsonElement result)
    {
        var status = result.TryGetProperty("status", out var providerStatus) ? providerStatus.GetString() : null;
        if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!result.TryGetProperty("ssl", out var ssl)) return false;
        var certificateStatus = ssl.TryGetProperty("status", out var sslStatus) ? sslStatus.GetString() : null;
        return !string.Equals(certificateStatus, "active", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> RoutingConfirmedAsync(WebsiteDomainBinding binding, CancellationToken ct)
    {
        using var handler = LegendConnectResearchNetworkPolicy.CreatePublicReadOnlyHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await client.GetAsync("https://" + binding.Hostname + "/.well-known/legend-website", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4097];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > 4096) return false;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            return json.RootElement.TryGetProperty("businessId", out var business) && business.TryGetGuid(out var businessId) && businessId == binding.CommerceBusinessId &&
                json.RootElement.TryGetProperty("bindingId", out var value) && value.TryGetGuid(out var bindingId) && bindingId == binding.Id;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    public string CnameTarget => configuration["WebsiteDomains:CnameTarget"] ?? "";
    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(configuration["WebsiteDomains:CloudflareZoneId"]) ||
            string.IsNullOrWhiteSpace(configuration["WebsiteDomains:ApiToken"]) || string.IsNullOrWhiteSpace(CnameTarget))
            throw new InvalidOperationException("Customer domain hosting is not configured.");
    }

    private async Task<JsonElement> CallAsync(HttpMethod method, string suffix, object? body, CancellationToken ct)
    {
        EnsureConfigured();
        var zone = Uri.EscapeDataString(configuration["WebsiteDomains:CloudflareZoneId"]!);
        using var request = new HttpRequestMessage(method, "https://api.cloudflare.com/client/v4/zones/" + zone + "/custom_hostnames" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration["WebsiteDomains:ApiToken"]);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await clients.CreateClient("WebsiteDomains").SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        if (!json.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Domain provider did not confirm the operation.");
        return json.RootElement.GetProperty("result").Clone();
    }
}
