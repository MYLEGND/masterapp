using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;

namespace AgentPortal.Services;

public sealed record FounderCallRelayStatus(string Status, string Detail, DateTime ObservedUtc,
    bool CanStart = false, bool CanStop = false, string? ResourceName = null, bool SetupRequired = false);
public sealed record FounderCallRelayOperation(bool Succeeded, string Status, string Detail);

/// <summary>Controls only the explicitly configured relay VM with the host's existing Azure identity.
/// It cannot provision resources, accept credentials, or manage arbitrary subscription resources.</summary>
public sealed class FounderCallRelayService(
    IConfiguration configuration, IHttpClientFactory clients, ILogger<FounderCallRelayService> logger, TokenCredential? credential = null)
{
    private readonly TokenCredential _credential = credential ?? new DefaultAzureCredential();
    private const string ApiVersion = "2025-04-01";
    private const string Scope = "https://management.azure.com/.default";

    internal static string? NormalizeResourceId(string? value) => value is not null && Regex.IsMatch(value,
        @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[a-zA-Z0-9_.()-]+/providers/Microsoft\.Compute/virtualMachines/[a-zA-Z0-9_-]+$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? value : null;

    public Task<FounderCallRelayStatus> GetAsync(CancellationToken cancellationToken) =>
        GetResourceAsync(NormalizeResourceId(configuration["Calling:Relay:ResourceId"]), cancellationToken);

    private async Task<FounderCallRelayStatus> GetResourceAsync(string? resource, CancellationToken cancellationToken)
    {
        if (resource is null)
            return new("Setup required", "An Azure relay has not been connected. Initial hosting and access setup must finish before activation is available.",
                DateTime.UtcNow, SetupRequired: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await SendAsync(HttpMethod.Get, resource + "/instanceView", deadline.Token);
            if (!response.IsSuccessStatusCode)
                return new("Unavailable", SafeFailure(response.StatusCode), DateTime.UtcNow, ResourceName: resource.Split('/')[^1]);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
            var state = body.RootElement.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array
                ? statuses.EnumerateArray().Select(item => item.TryGetProperty("code", out var code) ? code.GetString() : null)
                    .FirstOrDefault(code => code?.StartsWith("PowerState/", StringComparison.Ordinal) == true)
                : null;
            var urls = configuration.GetSection("Calling:Relay:Urls").GetChildren().Select(x => x.Value ?? "").ToArray();
            var configured = Shared.Calling.LegendCallRelay.IsConfigurationValid(urls, configuration["Calling:Relay:SharedSecret"]);
            return state switch
            {
                "PowerState/running" => new("Running", "Azure hosting is running. A completed relayed call is still required to verify media connectivity.", DateTime.UtcNow,
                    CanStop: true, ResourceName: resource.Split('/')[^1]),
                "PowerState/deallocated" or "PowerState/stopped" => new("Stopped", configured
                    ? "Activate to resume relay hosting. Compute and bandwidth charges may apply."
                    : "Hosting is stopped. Relay endpoint and credential configuration must be completed before activation.", DateTime.UtcNow,
                    CanStart: configured, ResourceName: resource.Split('/')[^1], SetupRequired: !configured),
                "PowerState/starting" or "PowerState/stopping" or "PowerState/deallocating" => new("Changing", "Azure is applying the hosting change. Refresh for its completed state.", DateTime.UtcNow,
                    ResourceName: resource.Split('/')[^1]),
                _ => new("Unknown", "Azure did not return a recognized hosting state. Activation remains unavailable.", DateTime.UtcNow, ResourceName: resource.Split('/')[^1])
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new("Unavailable", "Azure did not respond within the status deadline. No hosting state is assumed.", DateTime.UtcNow); }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException or HttpRequestException or JsonException)
        {
            logger.LogWarning("Relay status unavailable: {Reason}", error.GetType().Name);
            return new("Unavailable", "Azure hosting status could not be verified. Check the connected identity and retry.", DateTime.UtcNow);
        }
    }

    public async Task<FounderCallRelayOperation> ChangeAsync(string? action, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed || action is not ("start" or "stop"))
            return new(false, "Confirmation required", "Confirm the hosting action and its cost or call impact.");
        var resource = NormalizeResourceId(configuration["Calling:Relay:ResourceId"]);
        var status = await GetResourceAsync(resource, cancellationToken);
        if (action == "start" ? !status.CanStart : !status.CanStop)
            return new(false, status.Status, status.Detail);
        if (resource is null) return new(false, "Setup required", "The configured relay resource is unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // No automatic retry: an ambiguous response must be reconciled through the GET status.
            using var response = await SendAsync(HttpMethod.Post, resource + (action == "start" ? "/start" : "/deallocate"), deadline.Token);
            logger.LogInformation("Founder relay hosting action {Action}: Azure HTTP {Status}", action, (int)response.StatusCode);
            return response.IsSuccessStatusCode
                ? new(true, "Accepted", "Azure accepted the request. Refresh hosting status to confirm completion.")
                : new(false, "Unavailable", SafeFailure(response.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "Unconfirmed", "Azure's response timed out. Refresh hosting status before attempting another change."); }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException or HttpRequestException)
        {
            logger.LogWarning("Relay hosting action {Action} unconfirmed: {Reason}", action, error.GetType().Name);
            return new(false, "Unconfirmed", "The hosting change could not be confirmed. Refresh status before retrying.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
        using var request = new HttpRequestMessage(method, "https://management.azure.com" + path + "?api-version=" + ApiVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await clients.CreateClient("FounderCallRelay").SendAsync(request, cancellationToken);
    }

    private static string SafeFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "The connected Azure identity does not have the required relay access.",
        HttpStatusCode.NotFound => "The configured Azure relay resource was not found.",
        HttpStatusCode.TooManyRequests => "Azure is limiting management requests. Wait briefly and refresh.",
        _ => "Azure could not complete the relay management request. Refresh before retrying."
    };
}
