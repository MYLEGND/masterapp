using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Domain.Social;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Shared.Workspaces;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class BusinessAssistantImageSqlTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=";

    [PersistentAssistantFact]
    public async Task GenerationEditAndRevocationUseScopedMediaAndOneBoundProviderAttempt()
    {
        var connection = Environment.GetEnvironmentVariable("MASTERAPP_LOCAL_DB_CONNECTION")!;
        var target = new SqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1,14339", target.DataSource); Assert.Equal("MasterAppLocalTest", target.InitialCatalog);
        await using (var sql = new SqlConnection(connection))
        {
            await sql.OpenAsync(); await using var command = sql.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(100),value) FROM sys.extended_properties WHERE class=0 AND name=N'LegendLocalTestIdentity'";
            Assert.Equal(Environment.GetEnvironmentVariable("LEGEND_TEST_DB_IDENTITY"), await command.ExecuteScalarAsync());
        }
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(connection).Options);
        var actor = Guid.NewGuid().ToString("D");
        var profile = new AgentProfile { Id = Guid.NewGuid(), AgentUserId = actor, AgentUpn = "synthetic-image@local-test.invalid", FullName = "Synthetic image", IsActive = true };
        var tracking = new AgentTrackingProfile { AgentUserId = actor, AgentUpn = profile.AgentUpn, Slug = "synthetic-" + Guid.NewGuid().ToString("N") };
        db.AddRange(profile, tracking); await db.SaveChangesAsync();
        var owner = MarketingOwnerScope.Agent(tracking.Id);
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", actor);
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var config = new BusinessAssistantConfiguration("openai", "proj_synthetic", "org_synthetic", "synthetic-text",
            Guid.NewGuid(), Guid.NewGuid(), 25, 25, 1, 1, 512, new string('a', 64), DateTime.UtcNow.AddHours(1),
            Enabled: true, ImageModel: "gpt-image-2.5-sunburst", ImageQuality: "high", ImagesEnabled: true);
        try
        {
            var snapshot = await store.SaveAssistantConfigurationAsync(owner, config, Guid.Empty, "synthetic-key", _ => Task.CompletedTask);
            var storage = new Mock<ISocialMediaStorage>(MockBehavior.Strict);
            storage.Setup(x => x.StoreAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, string name, long size, Stream _, CancellationToken _) => SocialMediaStorageResult.Success(new(name, name, "image", "image/png", size, "synthetic/" + id)));
            storage.Setup(x => x.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SocialMediaReadResult.Available(new MemoryStream(Convert.FromBase64String(Png))));
            await using var services = new ServiceCollection().AddSingleton(db).AddSingleton(store)
                .AddSingleton(new WebsiteMediaService(db, storage.Object)).BuildServiceProvider();
            using var handler = new ImageHandler();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(x => x.CreateClient("OpenAI")).Returns(() => new HttpClient(handler, false) { BaseAddress = new Uri("https://api.openai.com/") });
            var legend = new FounderLegendConnectService(Mock.Of<ILegendConnectOperations>(), new AgentProfileAccessResolver(db));
            var service = new LegendFounderAiConversationService(factory.Object, new ConfigurationBuilder().Build(), legend,
                NullLogger<LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(null!, null!, Mock.Of<ILegendConnectOperations>()),
                Mock.Of<ILegendLanguageRegistry>(), Mock.Of<ITranslationService>(), Mock.Of<IControlledResourceAccessService>(),
                services.GetRequiredService<IServiceScopeFactory>());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("oid", actor) }, "synthetic"));
            var method = typeof(LegendFounderAiConversationService).GetMethod("CreateBusinessImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task<WebsiteMediaAsset> Generate(params Guid[] references) => (Task<WebsiteMediaAsset>)method.Invoke(service,
                new object[] { services, principal, owner, config, snapshot.Revision, "Synthetic test image", references, CancellationToken.None })!;
            var generated = await Generate();
            Assert.Equal(actor, generated.OwnerKey); Assert.Equal(1, handler.Attempts);
            Assert.Equal("/v1/images/generations", handler.Path);
            Assert.Equal("gpt-image-2.5-sunburst", handler.Model); Assert.Equal("high", handler.Quality); Assert.Equal("1", handler.Count);
            Assert.Equal("proj_synthetic", handler.Project); Assert.Equal("org_synthetic", handler.Organization);
            var edited = await Generate(generated.Id);
            Assert.Equal(generated.Id, edited.Id); Assert.Equal(2, handler.Attempts);
            Assert.Equal("/v1/images/edits", handler.Path); Assert.Equal(1, handler.ReferenceCount);
            await Assert.ThrowsAsync<ArgumentException>(() => Generate(Guid.NewGuid()));
            Assert.Equal(2, handler.Attempts);
            handler.BeforeResponse = async () => { tracking.Status = "disabled"; await db.SaveChangesAsync(); };
            await Assert.ThrowsAnyAsync<Exception>(() => Generate());
            Assert.Equal(3, handler.Attempts);
            Assert.Equal(1, await db.Set<WebsiteMediaAsset>().CountAsync(x => x.OwnerKey == actor));
            tracking.Status = "active"; await db.SaveChangesAsync(); handler.BeforeResponse = null; handler.Reject = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Generate());
            Assert.Equal(4, handler.Attempts);
            storage.Verify(x => x.StoreAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
            handler.Reject = false;
            var metadata = JsonSerializer.SerializeToElement(await service.InspectBusinessModelAccessAsync(
                principal, WorkspaceKey.ForAgentTrackingProfile(tracking.Id).Value, CancellationToken.None));
            Assert.Equal("metadata_inspection_only", metadata.GetProperty("status").GetString());
            Assert.Equal("proj_synthetic", metadata.GetProperty("project").GetString());
            Assert.Equal(2, metadata.GetProperty("models").GetArrayLength());
            Assert.All(metadata.GetProperty("models").EnumerateArray(), item =>
                Assert.Equal("model_access_observed", item.GetProperty("status").GetString()));
            Assert.Equal(6, handler.Attempts);
            Assert.DoesNotContain("synthetic-key", metadata.GetRawText());
            await Assert.ThrowsAnyAsync<Exception>(() => service.InspectBusinessModelAccessAsync(
                principal, WorkspaceKey.ForAgentTrackingProfile(Guid.NewGuid()).Value, CancellationToken.None));
            Assert.Equal(6, handler.Attempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
            // Use a fresh context because the scoped service provider owns its context.
            await using var cleanup = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlServer(connection).Options);
            await cleanup.Set<WebsiteMediaAsset>().Where(x => x.OwnerKey == actor).ExecuteDeleteAsync();
            await cleanup.MarketingConnections.Where(x => x.OwnerKey == owner.Key).ExecuteDeleteAsync();
            await cleanup.AgentTrackingProfiles.Where(x => x.Id == tracking.Id).ExecuteDeleteAsync();
            await cleanup.AgentProfiles.Where(x => x.Id == profile.Id).ExecuteDeleteAsync();
        }
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public int Attempts, ReferenceCount;
        public string? Path, Model, Quality, Count, Project, Organization;
        public bool Reject;
        public Func<Task>? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++; Path = request.RequestUri!.AbsolutePath;
            Project = request.Headers.GetValues("OpenAI-Project").Single(); Organization = request.Headers.GetValues("OpenAI-Organization").Single();
            if (request.Method == HttpMethod.Get)
            {
                Assert.StartsWith("/v1/models/", Path);
                Assert.Null(request.Content);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    JsonSerializer.Serialize(new { id = Uri.UnescapeDataString(Path[11..]) })) };
            }
            if (request.Content is MultipartFormDataContent form)
            {
                var fields = new Dictionary<string, string>();
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "image[]") { ReferenceCount++; Assert.Equal(Convert.FromBase64String(Png), await part.ReadAsByteArrayAsync(ct)); }
                    else fields[name] = await part.ReadAsStringAsync(ct);
                }
                Model = fields["model"]; Quality = fields["quality"]; Count = fields["n"];
            }
            else
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Model = json.RootElement.GetProperty("model").GetString(); Quality = json.RootElement.GetProperty("quality").GetString();
                Count = json.RootElement.GetProperty("n").GetInt32().ToString();
            }
            if (BeforeResponse is not null) await BeforeResponse();
            return new HttpResponseMessage(Reject ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Png } } })) };
        }
    }
}
