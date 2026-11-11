using System.Security.Claims;
using AgentPortal.Services.Analytics;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentPortal.Services;

public sealed partial class LegendFounderAiConversationService
{
    public sealed record ChatModelOption(string Id, string Provider, string Model, string Label, bool Configured);

    // Provider configuration remains the authority. This is a projection, not a
    // second model or credential registry. Metadata is not proof of paid capacity.
    public async Task<IReadOnlyList<ChatModelOption>> GetChatModelsAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        AgentPortal.Security.FounderGuard.EnsureFounderOrThrow(principal);
        var options = ConfiguredChatModels().ToList();
        var key = OpenAiKeyResolver.Resolve(_configuration);
        if (!string.IsNullOrWhiteSpace(key))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _httpClientFactory.CreateClient("OpenAI").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(1_048_576, deadline.Token);
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                var visible = document.RootElement.GetProperty("data").EnumerateArray()
                    .Select(x => x.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
                options = options.Select(x => x.Provider == "openai" ? x with { Configured = visible.Contains(x.Model) } : x).ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                options = options.Select(x => x.Provider == "openai" ? x with { Configured = false } : x).ToList();
            }
        }
        if (_configuration["LegendConnect:Foundation:HostKind"] == "Cloudflare")
        {
            var live = await _legend.GetCloudflareFoundationStatusAsync(principal, ct);
            var enabled = live.Reachable && live.Budget is { Paused: false, RemainingMicrousd: > 0 };
            options = options.Select(x => x.Provider == "cloudflare" ? x with { Configured = x.Configured && enabled } : x).ToList();
            foreach (var model in CloudflareManualSelectionReady ? live.Models : [])
                options.Add(new("cloudflare:" + model.Id, "cloudflare", model.Id, "Cloudflare · " + model.Id, enabled));
        }
        return options;
    }

    private bool CloudflareManualSelectionReady =>
        _configuration["LegendConnect:Foundation:Cloudflare:ProtocolVersion"] == "legend-cloudflare.v2";

    internal IReadOnlyList<ChatModelOption> ConfiguredChatModels()
    {
        var result = new List<ChatModelOption>();
        var openAiConfigured = !string.IsNullOrWhiteSpace(OpenAiKeyResolver.Resolve(_configuration));
        var models = _configuration.GetSection("OpenAI:LegendFounderAiModels").GetChildren()
            .Select(x => x.Value).Append(ResolveProviderModel())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())
            .Where(x => x.Length <= 160 && !x.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.Ordinal).Take(30);
        foreach (var model in models)
            result.Add(new("openai:" + model, "openai", model, "OpenAI · " + model, openAiConfigured));
        var foundation = _configuration["LegendConnect:Foundation:Model"]?.Trim();
        if (!string.IsNullOrWhiteSpace(foundation) &&
            !(_configuration["LegendConnect:Foundation:HostKind"] == "Cloudflare" &&
              foundation.StartsWith("@cf/", StringComparison.Ordinal) && !CloudflareManualSelectionReady))
        {
            var provider = _configuration["LegendConnect:Foundation:HostKind"] == "Cloudflare" ? "cloudflare" : "local";
            result.Add(new(provider + ":" + foundation, provider, foundation,
                (provider == "cloudflare" ? "Cloudflare" : "Local") + " · " + foundation,
                _configuration.GetValue<bool>("LegendConnect:Foundation:Enabled") && _modelInference is not null &&
                (provider != "cloudflare" || !foundation.StartsWith("@cf/", StringComparison.Ordinal) || CloudflareManualSelectionReady)));
        }
        return result;
    }

    internal ChatModelOption? ResolveChatModel(string selection, bool nativeOnly, bool externalAnsweringBlocked)
    {
        var choices = ConfiguredChatModels().Where(x => x.Configured &&
            (!(nativeOnly || externalAnsweringBlocked) || x.Provider == "local")).ToArray();
        // Auto chooses once before the turn. A provider failure never switches it.
        if (selection == "auto")
            return choices.FirstOrDefault(x => x.Provider == "openai" && x.Model == ResolveProviderModel())
                ?? choices.FirstOrDefault(x => x.Provider == "cloudflare")
                ?? choices.FirstOrDefault(x => x.Provider == "local");
        if (CloudflareManualSelectionReady && !(nativeOnly || externalAnsweringBlocked) && selection.StartsWith("cloudflare:@cf/", StringComparison.Ordinal) &&
            selection.Length <= 210 && selection.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '/' or '-' or '_' or '.' or '@') &&
            choices.Any(x => x.Provider == "cloudflare"))
            // The signed Worker registry is the canonical model-qualification,
            // budget and capability authority; it must honor this exact ID or fail.
            return new(selection, "cloudflare", selection["cloudflare:".Length..], selection, true);
        return choices.SingleOrDefault(x => x.Id == selection);
    }
}
