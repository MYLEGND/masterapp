using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.WebsiteEditing;

namespace Infrastructure.WebsitePublishing;

/// <summary>Compiles immutable public pages through the same JavaScript renderer used by the editor.</summary>
public sealed class WebsitePageCompiler(IWebHostEnvironment environment, IConfiguration configuration,
    ILogger<WebsitePageCompiler>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding ProcessUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    public const int MaxOutputCharacters = 32_000_000;

    public Task<string> CompileAsync(
        WebsiteContentDocument document,
        CommerceBusiness business,
        WebsiteBusinessFacts facts,
        CancellationToken cancellationToken) =>
        CompileAsync(document, business, facts, new Dictionary<string, WebsiteCollectionProjection>(StringComparer.Ordinal), cancellationToken);

    public async Task<string> CompileAsync(
        WebsiteContentDocument document,
        CommerceBusiness business,
        WebsiteBusinessFacts facts,
        IReadOnlyDictionary<string, WebsiteCollectionProjection> collections,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
            {
                document,
                business = new { id = business.Id, key = business.Key, displayName = business.DisplayName, legalName = business.LegalName,
                    businessType = business.BusinessType, contactEmail = facts.ContactEmail, contactPhone = facts.Phone,
                    hours = facts.Hours, locations = facts.Locations, services = facts.Services },
                commerce = new
                {
                    publicBaseUrl = (configuration["Commerce:PublicBaseUrl"] ?? "https://shopparfait.com").TrimEnd('/')
                },
                collections = collections.Values
            }, JsonOptions);
        return await CompileInputAsync(input, cancellationToken);
    }

    // Only the most recent immutable public payload is retained. The serialized
    // input is the cache identity, so a new publication cannot reuse old HTML.
    private readonly SemaphoreSlim legendCompilation = new(1, 1);
    private string? legendInputHash;
    private string? legendCompiledPages;

    public async Task<string> CompileLegendAsync(string publicPayload, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicPayload)));
        await legendCompilation.WaitAsync(cancellationToken);
        try
        {
            if (legendInputHash == hash && legendCompiledPages is not null) return legendCompiledPages;
            var compiled = await CompileInputAsync(publicPayload, cancellationToken);
            legendInputHash = hash;
            legendCompiledPages = compiled;
            return compiled;
        }
        finally { legendCompilation.Release(); }
    }

    private async Task<string> CompileInputAsync(string input, CancellationToken cancellationToken)
    {
        var root = configuration["WebsitePublishing:CompilerRoot"]
            ?? Path.Combine(environment.ContentRootPath, "WebsiteCompiler");
        var runner = Path.Combine(root, "scripts", "render-business-cli.mjs");
        if (!File.Exists(runner) || !Directory.Exists(Path.Combine(root, "node_modules", "linkedom")))
            throw new InvalidOperationException("website_compiler_not_configured");
        var info = new ProcessStartInfo(configuration["WebsitePublishing:NodeExecutable"] ?? "node")
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = ProcessUtf8, StandardOutputEncoding = ProcessUtf8, StandardErrorEncoding = ProcessUtf8,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(runner);
        using var process = new Process { StartInfo = info };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("website_compiler_not_available");
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters, bounded.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 16_384, bounded.Token);
            await process.StandardInput.WriteAsync(input.AsMemory(), bounded.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(bounded.Token);
            var output = await outputTask;
            var stderr = await errorTask;
            if (process.ExitCode != 0)
            {
                var diagnostic = DecodeFailureDiagnostic(stderr);
                logger?.LogWarning(
                    "Website compilation failed: phase={Phase}, category={Category}, exitCode={ExitCode}, elapsedMs={ElapsedMs}, childElapsedMs={ChildElapsedMs}",
                    diagnostic.Phase, diagnostic.Category, process.ExitCode,
                    elapsed.ElapsedMilliseconds, diagnostic.ElapsedMs);
                throw new InvalidOperationException("website_compilation_failed");
            }
            using var compiled = JsonDocument.Parse(output);
            if (!compiled.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object || !pages.EnumerateObject().Any())
                throw new InvalidOperationException("website_compilation_empty");
            return output;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static (string Phase, string Category, long? ElapsedMs) DecodeFailureDiagnostic(string stderr)
    {
        var fallback = (Phase: "unknown", Category: "unclassified_process_failure", ElapsedMs: (long?)null);
        if (stderr.Length > 1024) return fallback;
        try
        {
            using var parsed = JsonDocument.Parse(stderr);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
                schema.GetString() != "legend-website-compiler-diagnostic/v1" ||
                !root.TryGetProperty("phase", out var phaseValue) || phaseValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("category", out var categoryValue) || categoryValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("elapsedMs", out var durationValue) || durationValue.ValueKind != JsonValueKind.Number ||
                !durationValue.TryGetInt64(out var duration) || duration < 0)
                return fallback;
            var phase = phaseValue.GetString();
            var category = categoryValue.GetString();
            if (phase is not ("module_load" or "input_read" or "input_parse" or "render" or "output") ||
                category is not ("module_missing" or "file_missing" or "file_access_denied" or "render_timeout" or
                    "input_limit" or "invalid_input" or "compiler_error"))
                return fallback;
            return (phase!, category!, duration);
        }
        catch (JsonException) { return fallback; }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var value = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (value.Length + count > maximum) throw new InvalidOperationException("website_compilation_output_limit");
            value.Append(buffer, 0, count);
        }
        return value.ToString();
    }
}
