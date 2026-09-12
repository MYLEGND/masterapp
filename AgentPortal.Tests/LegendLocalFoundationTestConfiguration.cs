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
        if (!string.IsNullOrWhiteSpace(endpoint)) RequireControlledTestHost(resourceId);
        return builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Foundation:Enabled"] = (!string.IsNullOrWhiteSpace(endpoint)).ToString(),
            ["LegendConnect:Foundation:Endpoint"] = endpoint,
            ["LegendConnect:Foundation:AzureResourceId"] = resourceId,
            ["LegendConnect:Foundation:Model"] = Setting("MODEL"),
            ["LegendConnect:Foundation:ModelRevision"] = Setting("REVISION"),
            ["LegendConnect:Foundation:ApiKey"] = Setting("KEY"),
            ["LegendConnect:Foundation:Engine"] = "Vllm",
            ["LegendConnect:Foundation:EngineVersion"] = Setting("ENGINE_VERSION"),
            ["LegendConnect:Foundation:ToolCallParser"] = Setting("TOOL_CALL_PARSER"),
            ["LegendConnect:Foundation:ReasoningParser"] = "qwen3",
            ["LegendConnect:Foundation:EnableThinking"] = Setting("ENABLE_THINKING") ?? "false",
            ["LegendConnect:Foundation:ReasoningEffort"] = Setting("REASONING_EFFORT"),
            ["LegendConnect:Foundation:Temperature"] = Setting("TEMPERATURE") ?? "0",
            ["LegendConnect:Foundation:TopP"] = Setting("TOP_P") ?? "1",
            ["LegendConnect:Foundation:TopK"] = Setting("TOP_K") ?? "0",
            ["LegendConnect:Foundation:Seed"] = Setting("SEED") ?? "73",
            ["LegendConnect:Foundation:MaxContextTokens"] = Setting("CONTEXT_TOKENS") ?? "32768",
            ["LegendConnect:Foundation:MaxOutputTokens"] = Setting("OUTPUT_TOKENS") ?? "1024",
            ["LegendConnect:Foundation:TimeoutSeconds"] = Setting("TIMEOUT_SECONDS") ?? "120"
        });
    }

    private static string? Setting(string name) => Environment.GetEnvironmentVariable("LEGEND_CONTROLLED_FOUNDATION_" + name);

    private static readonly HashSet<string> VerifiedHosts = new(StringComparer.Ordinal);

    private static void RequireControlledTestHost(string? resourceId)
    {
        if (!OperatingSystem.IsLinux() || !LegendConnectModelInferenceTransport.IsControlledAzureResourceId(resourceId))
            throw new InvalidOperationException("Real model acceptance must run on the explicitly configured remote Azure Linux VM.");
        lock (VerifiedHosts)
        {
            if (VerifiedHosts.Contains(resourceId!)) return;
            var root = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "legend-local-foundation.py")))
                root = root.Parent;
            if (root is null) throw new InvalidOperationException("The authoritative remote host verifier source is unavailable.");
            var start = new ProcessStartInfo("python3")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root.FullName
            };
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(Path.Combine(root.FullName, "scripts", "legend-local-foundation.py"));
            start.ArgumentList.Add("--verify-host-only");
            start.ArgumentList.Add("--expected-azure-resource-id");
            start.ArgumentList.Add(resourceId!);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Remote host verification could not start.");
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Remote host verification exceeded its bounded deadline.");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("Remote host identity verification failed; no model test was executed.");
            using var receipt = JsonDocument.Parse(process.StandardOutput.ReadToEnd());
            if (receipt.RootElement.GetProperty("azure_resource_id").GetString() != resourceId ||
                receipt.RootElement.GetProperty("host_verification").GetString() != "azure-imds-resource-and-tag-v1")
                throw new InvalidOperationException("Remote test host receipt did not match the configured VM.");
            VerifiedHosts.Add(resourceId!);
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequireControlledTestHost(Setting("AZURE_RESOURCE_ID"));
            if (!Uri.TryCreate(Setting("ENDPOINT"), UriKind.Absolute, out var endpoint) || request.RequestUri != endpoint ||
                !LegendConnectModelInferenceTransport.IsControlledFoundationEndpoint(endpoint, authenticated: !string.IsNullOrWhiteSpace(Setting("KEY"))))
                throw new InvalidOperationException("The real-model fixture permits only the pinned authenticated controlled endpoint.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
