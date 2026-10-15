using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Infrastructure.Analytics;
using Domain.Entities;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>Repository-wide ownership constraints, including files linked into another host.</summary>
public sealed class AnalyticsCanonicalArchitectureTests
{
    private static string Root => Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
        ?? throw new InvalidOperationException("GITHUB_WORKSPACE must identify the audited repository.");

    private static IEnumerable<(string Path, string Source)> ProductionSources(string extension)
        => Directory.EnumerateFiles(Root, "*" + extension, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin" or "node_modules" or ".git" or "diagnostics" or "Migrations"
                    || part.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || part == "tests"))
            .Select(path => (Path.GetRelativePath(Root, path).Replace('\\', '/'), File.ReadAllText(path)));

    [Fact]
    public void OnlyCanonicalMapperAndWritersCanConstructAndPersistEventRows()
    {
        foreach (var (path, source) in ProductionSources(".cs"))
        {
            var text = Regex.Replace(source, @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
            if (Regex.IsMatch(text, @"new\s+(?:Domain\.Entities\.)?(AnalyticsEvent|MetaSignalEvent)\s*[{(]"))
                Assert.Equal("Infrastructure/Analytics/UnifiedEventMapper.cs", path);
            foreach (var (entity, writer) in new[]
            {
                ("AnalyticsEvent", "UnifiedAnalyticsWriter"), ("MetaSignalEvent", "UnifiedMetaSignalWriter")
            })
            {
                if (Regex.IsMatch(text, $@"\b{entity}s\s*\.\s*(Add|AddAsync|AddRange|AddRangeAsync|Attach|Update|UpdateRange)\s*\("))
                    Assert.Equal($"Infrastructure/Analytics/{writer}.cs", path);
                if (Regex.IsMatch(text, $@"\bSet\s*<\s*{entity}\s*>"))
                    Assert.Equal("Infrastructure/Data/MasterAppDbContext.cs", path);
                Assert.False(Regex.IsMatch(text, $@"\b(Add|AddAsync|AddRange|Attach|Update)\s*<\s*{entity}\s*>"), path);
                Assert.False(Regex.IsMatch(text, $@"(?i)(INSERT\s+(INTO\s+)?|MERGE\s+)(\[?dbo\]?\.)?\[?{entity}s\b"), path);
            }
            if (Regex.IsMatch(text, @"\bUnifiedMetaSignalWriter\s*\.\s*Write\s*\("))
                Assert.Equal("Infrastructure/Analytics/MetaSignalAnalyticsBridge.cs", path);
        }
    }

    [Fact]
    public void CompiledApplicationsHaveNoAlternativeTypedEfEventWriter()
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
        var assemblies = new[] { "Infrastructure", "AgentPortal", "ProtectWebsite", "ParfaitApp", "ClientApp", "Shared" }
            .Select(Assembly.Load);
        foreach (var assembly in assemblies)
        foreach (var type in assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            for (var offset = 0; offset < il.Length;)
            {
                var value = (ushort)il[offset++];
                if (value == 0xfe) value = (ushort)(0xfe00 | il[offset++]);
                var opcode = opcodes[value];
                if (opcode.OperandType == OperandType.InlineMethod)
                {
                    MethodBase? called;
                    try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset), type.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null); }
                    catch (ArgumentException) { called = null; }
                    if (called?.DeclaringType is { } declaring &&
                        (declaring.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true) &&
                        Regex.IsMatch(called.Name, @"^(Add|Attach|Update)(Range)?(Async)?$"))
                    {
                        var entities = declaring.GetGenericArguments().Concat(called.IsGenericMethod ? called.GetGenericArguments() : Type.EmptyTypes);
                        foreach (var entity in entities.Where(t => t == typeof(AnalyticsEvent) || t == typeof(MetaSignalEvent)))
                        {
                            var expected = entity == typeof(AnalyticsEvent) ? typeof(UnifiedAnalyticsWriter) : typeof(UnifiedMetaSignalWriter);
                            Assert.True(type == expected, $"{type.FullName}.{method.Name} bypasses {expected.Name} via {called}.");
                        }
                    }
                }
                offset += opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                    _ => 4
                };
            }
        }
    }

    [Fact]
    public void ReportingServicesDelegateProfileExpansionToOneAuthority()
    {
        foreach (var file in new[] { "AnalyticsQueryService", "MetaSignalAnalyticsService", "MetaAdsService" })
        {
            var text = File.ReadAllText(Path.Combine(Root, "Infrastructure", "Analytics", file + ".cs"));
            Assert.Contains("AnalyticsTrackingProfileScope", text);
            // The old copies selected UPNs then expanded profiles independently, with different Founder rules.
            Assert.DoesNotContain("Select(p => p.AgentUpn)", text);
            Assert.DoesNotContain("Select(x => x.AgentUpn)", text);
        }
        Assert.False(File.Exists(Path.Combine(Root, "AgentPortal/Services/Tracking/AgentTrackingResolver.cs")));
        Assert.False(File.Exists(Path.Combine(Root, "AgentPortal/Controllers/API/AnalyticsIngestController.cs")));
    }

    [Fact]
    public void PortalHasOneMetaConnectionRegistrationAndNoRedundantMetaServiceRegistration()
    {
        var program = File.ReadAllText(Path.Combine(Root, "AgentPortal/Program.cs"));
        Assert.DoesNotContain("AddScoped<IMetaAdsConnectionStore", program);
        Assert.DoesNotContain("AddScoped<IMetaAdsService", program);
        Assert.Single(Regex.Matches(program, @"Replace\s*\(\s*ServiceDescriptor\.Scoped<IMetaAdsConnectionStore,\s*MetaAdsConnectionStore>"));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        MarketingServiceRegistration.AddMarketingConnections(services);
        Assert.Single(services.Where(s => s.ServiceType == typeof(Infrastructure.Analytics.IMetaAdsConnectionStore)));
        Assert.Single(services.Where(s => s.ServiceType == typeof(Infrastructure.Analytics.IMetaAdsService)));
    }

    [Fact]
    public void ProviderReportingAndEditorAdaptersCannotReintroduceOwnerOrAccountFallbacks()
    {
        var reporting = File.ReadAllText(Path.Combine(Root, "Infrastructure/Analytics/MetaAdsService.cs"));
        Assert.Contains("CanonicalAdvertisingEventProjection.ResolveOwnerAsync", reporting);
        Assert.DoesNotContain("MetaAds:AccessToken", reporting);
        Assert.DoesNotContain("MetaAds:DefaultAccountId", reporting);
        Assert.DoesNotContain("MetaAds:AgentAccountMap", reporting);
        var editor = File.ReadAllText(Path.Combine(Root, "Infrastructure/WebsiteEditing/WebsitePlatformController.cs"));
        Assert.DoesNotContain("MarketingOwnerScope.Agent(", editor);
        var commerce = File.ReadAllText(Path.Combine(Root, "ParfaitApp/Controllers/CommerceManagementController.cs"));
        Assert.DoesNotContain("MarketingOwnerScope.Agent(", commerce);
        var contract = typeof(IMetaAdsConnectionStore).GetMethods().Select(m => m.Name).ToArray();
        Assert.Equal(new[] { "GetAsync" }, contract);
    }

    [Fact]
    public void DashboardLoadersHaveOneImplementationAndOneScriptInclude()
    {
        var sources = ProductionSources(".js").ToArray();
        foreach (var name in new[] { "loadDeviceIntelligence", "loadMarketingPerformance", "renderGrowthEconomics" })
        {
            var owners = sources.SelectMany(s => Regex.Matches(s.Source,
                $@"(?:function\s+{name}\s*\(|(?:const|let|var)\s+{name}\s*=)").Select(_ => s.Path)).ToArray();
            Assert.Equal("AgentPortal/wwwroot/js/website-analytics.js", Assert.Single(owners));
        }
        var view = File.ReadAllText(Path.Combine(Root, "AgentPortal/Views/WebsiteAnalytics/Index.cshtml"));
        Assert.Single(Regex.Matches(view, @"src=""[^""]*/website-analytics\.js(?:[?""])"));
    }

    [Fact]
    public void DurableMarketingAndWebsiteNotificationWorkersHaveOneControlPlaneHost()
    {
        var portal = File.ReadAllText(Path.Combine(Root, "AgentPortal", "Program.cs"));
        var protect = File.ReadAllText(Path.Combine(Root, "Protect-Website", "Program.cs"));
        var marketing = File.ReadAllText(Path.Combine(Root, "Infrastructure", "Analytics", "MarketingConnectionStore.cs"));
        var leads = File.ReadAllText(Path.Combine(Root, "Infrastructure", "Leads", "WebsiteLeadServiceRegistration.cs"));
        var parfaitMail = File.ReadAllText(Path.Combine(Root, "ParfaitApp", "Services", "GraphMailService.cs"));

        Assert.Contains("AddMarketingBackgroundWorkers(builder.Services, builder.Configuration)", portal, StringComparison.Ordinal);
        Assert.Contains("AddWebsiteLeadBackgroundWorkers(builder.Services)", portal, StringComparison.Ordinal);
        Assert.Contains("AddWebsiteLeadNotificationTransport(builder.Services)", protect, StringComparison.Ordinal);

        foreach (var worker in new[]
        {
            "MetaSignalAnalyticsBridge",
            "MetaSignalOutcomeDispatcherHostedService",
            "OpenAiConversionDispatcherHostedService"
        })
        {
            Assert.Contains($"AddHostedService<{worker}>()", marketing, StringComparison.Ordinal);
            Assert.DoesNotContain($"AddHostedService<Infrastructure.Analytics.{worker}>()", protect, StringComparison.Ordinal);
        }

        foreach (var worker in new[] { "BusinessInquiryNotificationWorker", "WebsiteLeadNotificationRecoveryWorker" })
        {
            Assert.Contains($"AddHostedService<{worker}>()", leads, StringComparison.Ordinal);
            Assert.DoesNotContain($"AddHostedService<{worker}>()", protect, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(Root, "Protect-Website", "Services", "Communication", "IProtectEmailSender.cs")));
        Assert.False(File.Exists(Path.Combine(Root, "Protect-Website", "Services", "Communication", "GraphProtectEmailSender.cs")));
        Assert.False(File.Exists(Path.Combine(Root, "Protect-Website", "Services", "Meta", "MetaCapiCredentialProtector.cs")));
        Assert.False(File.Exists(Path.Combine(Root, "AgentPortal", "Services", "MetaCapiCredentialProtector.cs")));
        Assert.True(File.Exists(Path.Combine(Root, "Infrastructure", "Leads", "GraphWebsiteInquiryEmailSender.cs")));
        Assert.True(File.Exists(Path.Combine(Root, "Infrastructure", "Analytics", "MetaCapiCredentialProtector.cs")));

        Assert.DoesNotContain("SendContactEmailsAsync", parfaitMail, StringComparison.Ordinal);
        Assert.DoesNotContain("Contact:RecipientEmail", parfaitMail, StringComparison.Ordinal);
    }
}
