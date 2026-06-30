using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ParfaitApp.Services;

public sealed class SquarePaymentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public SquarePaymentService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<(bool Success, string? PaymentId, string? Error)> CreatePaymentAsync(
        string sourceId,
        int amountCents,
        string note,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var accessToken = _configuration["Square:AccessToken"];
        var locationId = _configuration["Square:LocationId"];
        var environment = _configuration["Square:Environment"] ?? "Sandbox";

        if (string.IsNullOrWhiteSpace(accessToken))
            return (false, null, "Square access token is not configured.");

        if (string.IsNullOrWhiteSpace(locationId))
            return (false, null, "Square location ID is not configured.");

        if (amountCents <= 0)
            return (false, null, "Cart total must be greater than zero.");

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return (false, null, "Square payment idempotency key is missing.");

        var baseUrl = environment.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? "https://connect.squareup.com"
            : "https://connect.squareupsandbox.com";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v2/payments");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = new
        {
            idempotency_key = idempotencyKey,
            source_id = sourceId,
            location_id = locationId,
            amount_money = new
            {
                amount = amountCents,
                currency = "USD"
            },
            autocomplete = true,
            note
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return (false, null, json);

        using var doc = JsonDocument.Parse(json);

        var paymentId = doc.RootElement
            .GetProperty("payment")
            .GetProperty("id")
            .GetString();

        return (true, paymentId, null);
    }
}
