using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderSoftwareRemediationRulesetProtectionTests
{
    private const string BaseSha = "1111111111111111111111111111111111111111";
    private const string ApprovedRef = "refs/heads/legend/approved-changes";

    [Fact]
    public async Task ForbiddenClassicProtectionFallsBackToExactActiveRuleset()
    {
        using var fixture = new Fixture();
        var result = await fixture.VerifyAsync();

        Assert.True(result.GetProperty("wouldCreateIsolatedRepairBranch").GetBoolean(), result.ToString());
        Assert.True(result.GetProperty("wouldOpenPullRequest").GetBoolean(), result.ToString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
        Assert.Contains("/branches/legend%2Fapproved-changes/protection", fixture.Handler.Reads);
        Assert.Contains("/repos/MYLEGND/masterapp/rulesets", fixture.Handler.Reads);
        Assert.Contains("/repos/MYLEGND/masterapp/rulesets/77", fixture.Handler.Reads);
    }

    [Fact]
    public async Task RulesetForDifferentRefFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Handler.RulesetRef = "refs/heads/not-approved";

        var result = await fixture.VerifyAsync();

        Assert.False(result.GetProperty("wouldCreateIsolatedRepairBranch").GetBoolean(), result.ToString());
        Assert.Equal("authority_requirements_not_verified", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task InactiveRulesetFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Handler.RulesetEnforcement = "disabled";

        var result = await fixture.VerifyAsync();

        Assert.False(result.GetProperty("wouldCreateIsolatedRepairBranch").GetBoolean(), result.ToString());
    }

    [Fact]
    public async Task RulesetMissingRequiredCheckFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Handler.RequiredCheck = "some-other-check";

        var result = await fixture.VerifyAsync();

        Assert.False(result.GetProperty("wouldCreateIsolatedRepairBranch").GetBoolean(), result.ToString());
    }

    [Fact]
    public async Task RulesetWithBypassActorFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Handler.HasBypassActor = true;

        var result = await fixture.VerifyAsync();

        Assert.False(result.GetProperty("wouldCreateIsolatedRepairBranch").GetBoolean(), result.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);

        public RulesetHandler Handler { get; }
        public FounderSoftwareRemediationService Service { get; }

        public Fixture()
        {
            Handler = new RulesetHandler(_key.ExportPkcs8PrivateKeyPem());
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FounderSoftwareRemediation:Enabled"] = "true",
                    ["FounderSoftwareRemediation:RepositoryOwner"] = "MYLEGND",
                    ["FounderSoftwareRemediation:RepositoryName"] = "masterapp",
                    ["FounderSoftwareRemediation:BaseBranch"] = "legend/approved-changes",
                    ["FounderSoftwareRemediation:GitHubAppId"] = "1",
                    ["FounderSoftwareRemediation:GitHubInstallationId"] = "2",
                    ["FounderSoftwareRemediation:GitHubAppPrivateKeySecretUri"] = "https://fixture.vault.azure.net/secrets/app",
                    ["FounderSoftwareRemediation:GitHubApiBaseUri"] = "https://api.github.com/"
                })
                .Build();
            Service = new FounderSoftwareRemediationService(
                new ClientFactory(Handler),
                config,
                NullLogger<FounderSoftwareRemediationService>.Instance,
                new Credential());
        }

        public async Task<JsonElement> VerifyAsync() =>
            JsonSerializer.SerializeToElement(await Service.TestRepairPreparationAsync(CancellationToken.None));

        public void Dispose()
        {
            Handler.Dispose();
            _key.Dispose();
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RulesetHandler(string privateKey) : HttpMessageHandler
    {
        public List<string> Reads { get; } = [];
        public string RulesetRef { get; set; } = ApprovedRef;
        public string RulesetEnforcement { get; set; } = "active";
        public string RequiredCheck { get; set; } = "architecture-validation";
        public bool HasBypassActor { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                Reads.Add(path);

            if (request.RequestUri.Host == "fixture.vault.azure.net")
                return Task.FromResult(Json(new { value = privateKey }));

            if (path == "/app/installations/2/access_tokens")
                return Task.FromResult(Json(new
                {
                    token = "synthetic-installation",
                    permissions = new
                    {
                        contents = "write",
                        pull_requests = "write",
                        checks = "read"
                    }
                }));

            if (path == "/repos/MYLEGND/masterapp")
                return Task.FromResult(Json(new { id = 1209859492 }));

            if (path is "/repos/MYLEGND/masterapp/git/ref/heads/legend%2Fapproved-changes"
                or "/repos/MYLEGND/masterapp/git/ref/heads/legend/approved-changes")
                return Task.FromResult(Json(new { @object = new { sha = BaseSha } }));

            if (path is "/repos/MYLEGND/masterapp/branches/legend%2Fapproved-changes/protection"
                or "/repos/MYLEGND/masterapp/branches/legend/approved-changes/protection")
                return Task.FromResult(Json(new { message = "Resource not accessible by integration" }, HttpStatusCode.Forbidden));

            if (path == "/repos/MYLEGND/masterapp/rulesets")
                return Task.FromResult(Json(new[]
                {
                    new
                    {
                        id = 77,
                        target = "branch",
                        enforcement = RulesetEnforcement
                    }
                }));

            if (path == "/repos/MYLEGND/masterapp/rulesets/77")
                return Task.FromResult(Json(new
                {
                    id = 77,
                    target = "branch",
                    enforcement = RulesetEnforcement,
                    conditions = new
                    {
                        ref_name = new
                        {
                            include = new[] { RulesetRef },
                            exclude = Array.Empty<string>()
                        }
                    },
                    bypass_actors = HasBypassActor ? new[] { new { actor_id = 1 } } : Array.Empty<object>(),
                    current_user_can_bypass = "never",
                    rules = new object[]
                    {
                        new { type = "deletion" },
                        new { type = "non_fast_forward" },
                        new
                        {
                            type = "pull_request",
                            parameters = new
                            {
                                allowed_merge_methods = new[] { "merge" }
                            }
                        },
                        new
                        {
                            type = "required_status_checks",
                            parameters = new
                            {
                                strict_required_status_checks_policy = true,
                                required_status_checks = new[]
                                {
                                    new { context = RequiredCheck }
                                }
                            }
                        }
                    }
                }));

            if (path == $"/repos/MYLEGND/masterapp/commits/{BaseSha}/check-runs")
                return Task.FromResult(Json(new
                {
                    check_runs = new[]
                    {
                        new { name = "architecture-validation", conclusion = "success" }
                    }
                }));

            return Task.FromResult(Json(new { message = "not found", path }, HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json")
            };
    }
}
