using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Commerce.Payments.Providers;

public abstract class MerchantProviderTransport(IHttpClientFactory clients, IConfiguration configuration)
{
    protected string Setting(string provider, string environment, string name) => configuration[$"MerchantPayments:{provider}:{environment}:{name}"] ?? "";
    protected bool Enabled(string provider, string environment) => environment is "Sandbox" or "Production" && Setting(provider, environment, "Enabled").Equals("true", StringComparison.OrdinalIgnoreCase);
    protected bool WebhookConfigured(string provider,string environment,string verificationSetting) =>
        environment != "Production" || (!string.IsNullOrWhiteSpace(Setting(provider,environment,verificationSetting)) &&
        Uri.TryCreate(Setting(provider,environment,"WebhookUrl"),UriKind.Absolute,out var uri) && uri.Scheme=="https" && string.IsNullOrEmpty(uri.Fragment));
    protected static string Escape(string value) => Uri.EscapeDataString(value);
    protected static string S(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    protected static bool B(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    protected static long L(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : 0;
    protected static JsonElement P(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    protected static string Header(IReadOnlyDictionary<string,string> headers, string key) => headers.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";
    protected static void Require(bool condition, string code) { if (!condition) throw new MerchantPaymentException(code); }
    protected async Task<JsonElement> SendAsync(HttpMethod method, string url, string bearer, HttpContent? content, CancellationToken ct, params (string Name,string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrEmpty(bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        foreach (var (name,value) in headers) request.Headers.TryAddWithoutValidation(name,value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var response = await clients.CreateClient("MerchantPayments").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new MerchantPaymentException((int)response.StatusCode >= 500 || (int)response.StatusCode == 429 ? "PROVIDER_UNAVAILABLE" : "PROVIDER_REJECTED_REQUEST");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return JsonDocument.Parse("{}").RootElement.Clone();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MerchantPaymentException("PROVIDER_OUTCOME_UNKNOWN"); }
        catch (HttpRequestException) { throw new MerchantPaymentException("PROVIDER_OUTCOME_UNKNOWN"); }
        catch (JsonException) { throw new MerchantPaymentException("PROVIDER_INVALID_RESPONSE"); }
    }
    protected static HttpContent Json(object value) => JsonContent.Create(value);
    protected static HttpContent Form(params (string Name,string Value)[] fields) => new FormUrlEncodedContent(fields.Select(x => new KeyValuePair<string,string>(x.Name,x.Value)));
}
