using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectMetricsBackendHardeningTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authenticate")]
    [InlineData(HttpStatusCode.Forbidden, "not authorized")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.TooManyRequests, "throttled")]
    [InlineData(HttpStatusCode.InternalServerError, "temporarily unavailable")]
    [InlineData(HttpStatusCode.BadGateway, "temporarily unavailable")]
    [InlineData(HttpStatusCode.BadRequest, "HTTP 400")]
    public async Task AzureStatusExplainsActualFailureWithoutInventingCapacityOrLeakingResponse(HttpStatusCode status, string expected)
    {
        var handler = new StatusHandler(status);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureTranslator:ResourceId"] = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/translator"
        }).Build();
        var source = new AzureTranslatorSubscriptionCapacitySource(factory.Object, configuration,
            NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance, new Credential());
        var result = await source.GetCurrentAsync();
        Assert.False(result.IsAvailable);
        Assert.Equal("Unavailable", result.Status);
        Assert.Null(result.MonthlyIncludedCharacterAllowance);
        Assert.Null(result.MonthlyAzureReportedCharacters);
        Assert.Contains(expected, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-marker", result.Detail, StringComparison.Ordinal);
        Assert.Same(result, await source.GetCurrentAsync());
        Assert.Equal(1, handler.Calls); // Preserve the existing refresh boundary; no retry loop.
    }

    [Fact]
    public async Task ReadinessAggregatesAllCandidateStatesInSqlAndCountsCaseEquivalentPairsOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var observer = new Observer();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(observer).Options);
        await db.Database.EnsureCreatedAsync();
        var states = new[] { "Pending", "Processing", "Rejected", "Deduplicated", "Queued" };
        for (var i = 0; i < 1000; i++)
            db.Add(new LegendCorpusCandidate
            {
                IdempotencyKey = "readiness-" + i,
                IsApproved = i % 3 != 0,
                ProcessingState = states[i % states.Length],
                SourceLanguageCode = i % 2 == 0 ? "en" : "EN",
                TargetLanguageCode = i % 2 == 0 ? "fr" : "FR",
                SourceText = new string('x', 2000)
            });
        await db.SaveChangesAsync();
        var expected = db.ChangeTracker.Entries<LegendCorpusCandidate>().Select(item => item.Entity).ToArray();
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        registry.Setup(item => item.ListEnabledTranslationLanguagesReadOnlyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LegendLanguageDefinitionSnapshot>());
        var authority = new LegendConnectRuntimePolicyAuthority(db, Mock.Of<IControlledResourceAccessService>(), registry.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LegendConnect:ContextualComposition:Mode"] = "Shadow" }).Build(), NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        observer.Commands.Clear();
        var result = await authority.GetReadinessAsync(CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.Equal(expected.LongCount(item => item.IsApproved), result.ApprovedCandidateCount);
        Assert.Equal(expected.LongCount(item => item.IsApproved && item.ProcessingState is "Pending" or "Processing"), result.PendingCandidateCount);
        Assert.Equal(expected.LongCount(item => !item.IsApproved || item.ProcessingState == "Rejected"), result.RejectedOrIneligibleCandidateCount);
        Assert.Equal(expected.LongCount(item => item.ProcessingState == "Deduplicated"), result.DuplicateCandidateCount);
        Assert.Equal(1, result.AwaitingKnowledgePairCount);
        var candidateQueries = observer.Commands.Where(sql => sql.Contains("LegendCorpusCandidates", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, candidateQueries.Length);
        Assert.All(candidateQueries, sql =>
        {
            Assert.Contains("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SourceText", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("IdempotencyKey", sql, StringComparison.Ordinal);
        });
        registry.VerifyAll();
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("private-response-marker") });
        }
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Observer : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
