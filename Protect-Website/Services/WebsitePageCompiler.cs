using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.WebsiteEditing;

namespace ProtectWebsite.Services;

/// <summary>Compiles immutable public pages through the same JavaScript renderer used by the editor.</summary>
public sealed class WebsitePageCompiler(IWebHostEnvironment environment, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int MaxOutputCharacters = 32_000_000;

    public async Task<string> CompileAsync(WebsiteContentDocument document, CommerceBusiness business, WebsiteBusinessFacts facts, CancellationToken cancellationToken)
    {
        var root = configuration["WebsitePublishing:CompilerRoot"]
            ?? Path.Combine(environment.ContentRootPath, "WebsiteCompiler");
        var runner = Path.Combine(root, "scripts", "render-business.mjs");
        if (!File.Exists(runner) || !Directory.Exists(Path.Combine(root, "node_modules", "linkedom")))
            throw new InvalidOperationException("website_compiler_not_configured");
        var info = new ProcessStartInfo(configuration["WebsitePublishing:NodeExecutable"] ?? "node")
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(runner);
        using var process = new Process { StartInfo = info };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!process.Start()) throw new InvalidOperationException("website_compiler_not_available");
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters, bounded.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 16_384, bounded.Token);
            var input = JsonSerializer.Serialize(new
            {
                document,
                business = new { id = business.Id, displayName = business.DisplayName, legalName = business.LegalName,
                    businessType = business.BusinessType, contactEmail = facts.ContactEmail, contactPhone = facts.Phone,
                    hours = facts.Hours, locations = facts.Locations, services = facts.Services }
            }, JsonOptions);
            await process.StandardInput.WriteAsync(input.AsMemory(), bounded.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(bounded.Token);
            var output = await outputTask;
            _ = await errorTask; // Do not expose customer content or stack traces in the publish response.
            if (process.ExitCode != 0) throw new InvalidOperationException("website_compilation_failed");
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
