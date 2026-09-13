using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Infrastructure.Messaging;

namespace AgentPortal.Tests;

/// <summary>
/// Real controlled-model fixture configuration. This supplies no model answers;
/// capability tests still fail if the configured checkpoint cannot answer.
/// External providers keep their existing refusing/counting test boundary.
/// </summary>
internal static class LegendLocalFoundationTestConfiguration
{
    internal static IConfigurationBuilder AddControlledFoundation(this IConfigurationBuilder builder)
    {
        var endpoint = Environment.GetEnvironmentVariable("LEGEND_CONTROLLED_FOUNDATION_ENDPOINT");
        var resourceId = Environment.GetEnvironmentVariable("LEGEND_CONTROLLED_FOUNDATION_AZURE_RESOURCE_ID");
        var hostKind = Setting("HOST_KIND") ?? "AzureVm";
        var macHostId = Setting("MAC_HOST_ID");
        if (!string.IsNullOrWhiteSpace(endpoint)) RequireControlledTestHost(hostKind, resourceId, macHostId);
        return builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Foundation:Enabled"] = (!string.IsNullOrWhiteSpace(endpoint)).ToString(),
            ["LegendConnect:Foundation:Endpoint"] = endpoint,
            ["LegendConnect:Foundation:HostKind"] = hostKind,
            ["LegendConnect:Foundation:AzureResourceId"] = resourceId,
            ["LegendConnect:Foundation:MacHostId"] = macHostId,
            ["LegendConnect:Foundation:Model"] = Setting("MODEL"),
            ["LegendConnect:Foundation:ModelRevision"] = Setting("REVISION"),
            ["LegendConnect:Foundation:ApiKey"] = Setting("KEY"),
            ["LegendConnect:Foundation:StreamResponses"] = Setting("STREAM_RESPONSES") ?? "true",
            ["LegendConnect:Foundation:Engine"] = hostKind == "FounderMac" ? "Mlx" : "Vllm",
            ["LegendConnect:Foundation:EngineVersion"] = Setting("ENGINE_VERSION"),
            ["LegendConnect:Foundation:ToolCallParser"] = Setting("TOOL_CALL_PARSER"),
            ["LegendConnect:Foundation:ReasoningParser"] = "qwen3",
            ["LegendConnect:Foundation:EnableThinking"] = Setting("ENABLE_THINKING") ?? "false",
            ["LegendConnect:Foundation:ReasoningEffort"] = Setting("REASONING_EFFORT"),
            ["LegendConnect:Foundation:Temperature"] = Setting("TEMPERATURE") ?? "0",
            ["LegendConnect:Foundation:TopP"] = Setting("TOP_P") ?? "1",
            ["LegendConnect:Foundation:TopK"] = Setting("TOP_K") ?? "0",
            ["LegendConnect:Foundation:PresencePenalty"] = Setting("PRESENCE_PENALTY") ?? "0",
            ["LegendConnect:Foundation:Seed"] = Setting("SEED") ?? "73",
            ["LegendConnect:Foundation:MaxContextTokens"] = Setting("CONTEXT_TOKENS") ?? (hostKind == "FounderMac" ? "8192" : "32768"),
            ["LegendConnect:Foundation:MaxOutputTokens"] = Setting("OUTPUT_TOKENS") ?? "1024",
            ["LegendConnect:Foundation:TimeoutSeconds"] = Setting("TIMEOUT_SECONDS") ?? "120"
        });
    }

    private static string? Setting(string name) => Environment.GetEnvironmentVariable("LEGEND_CONTROLLED_FOUNDATION_" + name);

    private static readonly HashSet<string> VerifiedHosts = new(StringComparer.Ordinal);

    private static void RequireControlledTestHost(string hostKind, string? resourceId, string? macHostId)
    {
        var isMac = hostKind == "FounderMac";
        if (isMac)
        {
            if (!LegendConnectModelInferenceTransport.IsControlledMacHostId(macHostId))
                throw new InvalidOperationException("Real Mac model acceptance requires the explicitly configured Founder Mac identity.");
            if (Uri.TryCreate(Setting("ENDPOINT"), UriKind.Absolute, out var endpoint) && !endpoint.IsLoopback)
            {
                if (!LegendConnectModelInferenceTransport.IsControlledFoundationEndpoint(endpoint,
                        authenticated: !string.IsNullOrWhiteSpace(Setting("KEY")), hostKind: hostKind))
                    throw new InvalidOperationException("Remote Mac model acceptance requires the pinned authenticated TLS connector.");
                // The test runner may be Linux (CI or Azure). Its hardware cannot
                // attest the Mac. The existing model transport must validate the
                // actual serving receipt on every response; do not cache a host
                // verification merely because a connector URL is configured.
                return;
            }
            if (!OperatingSystem.IsMacOS())
                throw new InvalidOperationException("Loopback Mac model acceptance requires the verified Founder Mac test host.");
        }
        else if (hostKind != "AzureVm" || !OperatingSystem.IsLinux() ||
                 !LegendConnectModelInferenceTransport.IsControlledAzureResourceId(resourceId))
            throw new InvalidOperationException("Real Azure model acceptance must run on the explicitly configured remote Azure Linux VM.");
        var hostKey = hostKind + ":" + (isMac ? macHostId : resourceId);
        lock (VerifiedHosts)
        {
            if (VerifiedHosts.Contains(hostKey)) return;
            var root = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "legend-local-foundation.py")))
                root = root.Parent;
            if (root is null) throw new InvalidOperationException("The authoritative controlled host verifier source is unavailable.");
            var start = new ProcessStartInfo("python3")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root.FullName
            };
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(Path.Combine(root.FullName, "scripts", "legend-local-foundation.py"));
            start.ArgumentList.Add("--verify-host-only");
            if (isMac)
            {
                start.ArgumentList.Add("--host-kind");
                start.ArgumentList.Add("FounderMac");
            }
            start.ArgumentList.Add(isMac ? "--expected-mac-host-id" : "--expected-azure-resource-id");
            start.ArgumentList.Add((isMac ? macHostId : resourceId)!);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Controlled host verification could not start.");
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Controlled host verification exceeded its bounded deadline.");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("Controlled host identity verification failed; no model test was executed.");
            using var receipt = JsonDocument.Parse(process.StandardOutput.ReadToEnd());
            var rootReceipt = receipt.RootElement;
            var verified = isMac
                ? rootReceipt.GetProperty("host_kind").GetString() == "FounderMac" &&
                  rootReceipt.GetProperty("mac_host_id").GetString() == macHostId &&
                  rootReceipt.GetProperty("host_verification").GetString() == "macos-arm64-user-bound-v1"
                : rootReceipt.GetProperty("azure_resource_id").GetString() == resourceId &&
                  rootReceipt.GetProperty("host_verification").GetString() == "azure-imds-resource-and-tag-v1";
            if (!verified)
                throw new InvalidOperationException("Controlled test host receipt did not match the configured host.");
            VerifiedHosts.Add(hostKey);
        }
    }

    internal static HttpClient CreateControlledClient() => new(new ControlledEndpointHandler
    {
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false
        }
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class ControlledEndpointHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequireControlledTestHost(Setting("HOST_KIND") ?? "AzureVm", Setting("AZURE_RESOURCE_ID"), Setting("MAC_HOST_ID"));
            if (!Uri.TryCreate(Setting("ENDPOINT"), UriKind.Absolute, out var endpoint) || request.RequestUri != endpoint ||
                !LegendConnectModelInferenceTransport.IsControlledFoundationEndpoint(endpoint,
                    authenticated: !string.IsNullOrWhiteSpace(Setting("KEY")), hostKind: Setting("HOST_KIND") ?? "AzureVm"))
                throw new InvalidOperationException("The real-model fixture permits only the pinned authenticated controlled endpoint.");
            var capture = Environment.GetEnvironmentVariable("LEGEND_CONTROLLED_REQUEST_CAPTURE");
            if (!string.IsNullOrWhiteSpace(capture) && request.Content is not null)
            {
                // Opt-in synthetic test evidence only; never include request headers or keys.
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Length > 2_000_000 || File.Exists(capture) && new FileInfo(capture).Length > 20_000_000)
                    throw new InvalidOperationException("Controlled test evidence exceeded its bounded capture size.");
                await File.AppendAllTextAsync(capture, JsonSerializer.Serialize(new
                    { TimestampUtc = DateTimeOffset.UtcNow, Request = JsonSerializer.Deserialize<JsonElement>(body) }) + "\n", cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
