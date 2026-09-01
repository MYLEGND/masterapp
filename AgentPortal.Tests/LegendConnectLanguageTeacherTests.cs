using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectLanguageTeacherTests
{
    [Fact]
    public async Task UnconfiguredTeacher_FailsClosedWithoutNetworkCall()
    {
        var handler = new StubHttpMessageHandler();
        var teacher = CreateTeacher(
            handler,
            new Dictionary<string, string?>());

        var result =
            await teacher.ProposeAsync(
                ProposalRequest());

        var preflight = teacher.Preflight(
            LegendLanguageTeacherRole.Teacher);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Families);
        Assert.Equal(
            "language_teacher_configuration_missing",
            result.ErrorCode);
        Assert.False(preflight.IsReady);
        Assert.Equal(
            "language_teacher_configuration_missing",
            preflight.FailureCode);
        Assert.False(
            string.IsNullOrWhiteSpace(
                preflight.ConfigurationFingerprint));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void SharedOpenAiProviderConfiguration_ActivatesBothRoleSpecificBoundaries()
    {
        var handler = new StubHttpMessageHandler();
        var teacher = CreateTeacher(
            handler,
            new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "shared-provider-key",
                ["OpenAI:LegendFounderAiModel"] = "shared-provider-model"
            });

        var teacherPreflight = teacher.Preflight(
            LegendLanguageTeacherRole.Teacher);
        var criticPreflight = teacher.Preflight(
            LegendLanguageTeacherRole.Critic);

        Assert.True(teacherPreflight.IsReady, teacherPreflight.FailureCode);
        Assert.True(criticPreflight.IsReady, criticPreflight.FailureCode);
        Assert.NotEqual(
            teacherPreflight.ConfigurationFingerprint,
            criticPreflight.ConfigurationFingerprint);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(401, "language_teacher_authentication_failed")]
    [InlineData(403, "language_teacher_authentication_failed")]
    [InlineData(400, "language_teacher_schema_failed")]
    [InlineData(422, "language_teacher_schema_failed")]
    [InlineData(429, "language_teacher_quota_exceeded")]
    [InlineData(408, "language_teacher_timeout")]
    [InlineData(504, "language_teacher_timeout")]
    public async Task ProviderHttpFailure_IsClassifiedByFailureBoundary(
        int statusCode,
        string expectedFailureCode)
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            new HttpResponseMessage(
                (HttpStatusCode)statusCode));
        var teacher = CreateTeacher(
            handler,
            new Dictionary<string, string?>
            {
                ["LegendConnect:LanguageTeacher:ApiKey"] =
                    "test-key",
                ["LegendConnect:LanguageTeacher:TeacherModel"] =
                    "teacher-test-model"
            });

        var result = await teacher.ProposeAsync(
            ProposalRequest());

        Assert.False(result.Succeeded);
        Assert.Empty(result.Families);
        Assert.Equal(expectedFailureCode, result.ErrorCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Teacher_UsesStrictNonStoredStructuredResponse_AndReturnsOnlyProposal()
    {
        var handler = new StubHttpMessageHandler();

        handler.Enqueue(
            StructuredResponse(
                new
                {
                    families = new[]
                    {
                        new
                        {
                            family_key =
                                "generated.request.assistance",
                            semantic_category =
                                "Requesting assistance",
                            rationale =
                                "Controlled request contrast.",
                            confidence = 0.94m,
                            examples = new[]
                            {
                                new
                                {
                                    source_text =
                                        "I need your help.",
                                    target_text =
                                        (string?)"Mwen bezwen èd ou.",
                                    components = new[]
                                    {
                                        new
                                        {
                                            dimension = "agent",
                                            value = "I",
                                            surface_form = "I"
                                        },
                                        new
                                        {
                                            dimension = "predicate",
                                            value = "need",
                                            surface_form = "need"
                                        }
                                    }
                                },
                                new
                                {
                                    source_text =
                                        "We need your help.",
                                    target_text =
                                        (string?)"Nou bezwen èd ou.",
                                    components = new[]
                                    {
                                        new
                                        {
                                            dimension = "agent",
                                            value = "we",
                                            surface_form = "We"
                                        },
                                        new
                                        {
                                            dimension = "predicate",
                                            value = "need",
                                            surface_form = "need"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }));

        var teacher =
            CreateTeacher(
                handler,
                new Dictionary<string, string?>
                {
                    ["LegendConnect:LanguageTeacher:ApiKey"] =
                        "test-key",
                    ["LegendConnect:LanguageTeacher:TeacherModel"] =
                        "teacher-test-model"
                });

        var result =
            await teacher.ProposeAsync(
                ProposalRequest());

        Assert.True(
            result.Succeeded,
            result.ErrorCode);

        var family = Assert.Single(result.Families);

        Assert.Equal(
            "generated.request.assistance",
            family.FamilyKey);

        Assert.Equal(
            "Requesting assistance",
            family.SemanticCategory);

        Assert.Equal(2, family.Examples.Count);
        Assert.Equal(0.94m, family.Confidence);
        Assert.Equal("translation", family.CapabilityIdentity);
        Assert.Equal("reusable_semantic", family.CategoryIdentity);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(
            HttpMethod.Post,
            handler.LastMethod);

        Assert.Equal(
            "https://api.openai.com/v1/responses",
            handler.LastUri?.AbsoluteUri);

        Assert.Equal(
            "Bearer",
            handler.LastAuthorizationScheme);

        Assert.Equal(
            "test-key",
            handler.LastAuthorizationParameter);

        Assert.NotNull(handler.LastBody);

        using var body =
            JsonDocument.Parse(handler.LastBody!);

        Assert.Equal(
            "teacher-test-model",
            body.RootElement
                .GetProperty("model")
                .GetString());

        Assert.False(
            body.RootElement
                .GetProperty("store")
                .GetBoolean());

        var format =
            body.RootElement
                .GetProperty("text")
                .GetProperty("format");

        Assert.Equal(
            "json_schema",
            format
                .GetProperty("type")
                .GetString());

        Assert.True(
            format
                .GetProperty("strict")
                .GetBoolean());

        Assert.Equal(
            "legend_language_teacher",
            format
                .GetProperty("name")
                .GetString());
        using var input = JsonDocument.Parse(
            body.RootElement.GetProperty("input").GetString()!);
        Assert.Equal(
            "translation",
            input.RootElement.GetProperty("capability_identity").GetString());
        Assert.Equal(
            "reusable_semantic",
            input.RootElement.GetProperty("category_identity").GetString());
        Assert.Equal(
            "generated.request.assistance",
            input.RootElement.GetProperty("semantic_family_key").GetString());
        Assert.Equal(
            "Requesting assistance",
            input.RootElement.GetProperty("semantic_category").GetString());
    }

    [Fact]
    public async Task Critic_IsIndependentRole_AndCannotConvertRejectionIntoAuthority()
    {
        var handler = new StubHttpMessageHandler();

        handler.Enqueue(
            StructuredResponse(
                new
                {
                    approved = false,
                    confidence = 0.99m,
                    reason_codes = new[]
                    {
                        "target_not_supported_by_evidence",
                        "requires_canonical_validation"
                    }
                }));

        var teacher =
            CreateTeacher(
                handler,
                new Dictionary<string, string?>
                {
                    ["LegendConnect:LanguageTeacher:ApiKey"] =
                        "test-key",
                    ["LegendConnect:LanguageTeacher:CriticModel"] =
                        "critic-test-model"
                });

        var context = ProposalRequest();

        var proposal =
            new LegendLanguageTeacherFamilyProposal(
                "generated.request.assistance",
                "Requesting assistance",
                "Candidate only.",
                0.91m,
                [
                    new LegendLanguageTeacherExampleProposal(
                        "I need help.",
                        "Mwen bezwen èd.",
                        [
                            new LegendLanguageTeacherSemanticComponent(
                                "predicate",
                                "need",
                                "need")
                        ]),
                    new LegendLanguageTeacherExampleProposal(
                        "We need help.",
                        "Nou bezwen èd.",
                        [
                            new LegendLanguageTeacherSemanticComponent(
                                "predicate",
                                "need",
                                "need")
                        ])
                ]);

        var result =
            await teacher.CritiqueAsync(
                new LegendLanguageTeacherCritiqueRequest(
                    context,
                    proposal));

        Assert.True(
            result.Succeeded,
            result.ErrorCode);

        Assert.False(result.Approved);
        Assert.Equal(0.99m, result.Confidence);

        Assert.Contains(
            "target_not_supported_by_evidence",
            result.ReasonCodes);

        Assert.Equal(1, handler.RequestCount);

        using var body =
            JsonDocument.Parse(handler.LastBody!);

        Assert.Equal(
            "critic-test-model",
            body.RootElement
                .GetProperty("model")
                .GetString());

        Assert.Equal(
            "legend_language_critic",
            body.RootElement
                .GetProperty("text")
                .GetProperty("format")
                .GetProperty("name")
                .GetString());
        using var input = JsonDocument.Parse(
            body.RootElement.GetProperty("input").GetString()!);
        Assert.Equal(
            "translation",
            input.RootElement.GetProperty("capability_identity").GetString());
        Assert.Equal(
            "reusable_semantic",
            input.RootElement.GetProperty("category_identity").GetString());
        Assert.Equal(
            "generated.request.assistance",
            input.RootElement.GetProperty("semantic_family_key").GetString());
        Assert.Equal(
            "Requesting assistance",
            input.RootElement.GetProperty("semantic_category").GetString());
    }

    [Fact]
    public async Task MalformedProviderOutput_FailsClosedWithoutReturningProposal()
    {
        var handler = new StubHttpMessageHandler();

        handler.Enqueue(
            StructuredRawResponse(
                """
                {
                  "families": []
                }
                """));

        var teacher =
            CreateTeacher(
                handler,
                new Dictionary<string, string?>
                {
                    ["LegendConnect:LanguageTeacher:ApiKey"] =
                        "test-key",
                    ["LegendConnect:LanguageTeacher:TeacherModel"] =
                        "teacher-test-model"
                });

        var result =
            await teacher.ProposeAsync(
                ProposalRequest());

        Assert.False(result.Succeeded);
        Assert.Empty(result.Families);

        Assert.Equal(
            "language_teacher_parsing_failed",
            result.ErrorCode);
    }

    private static OpenAiLegendConnectLanguageTeacher CreateTeacher(
        StubHttpMessageHandler handler,
        IReadOnlyDictionary<string, string?> settings)
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

        return new OpenAiLegendConnectLanguageTeacher(
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<
                OpenAiLegendConnectLanguageTeacher>.Instance);
    }

    private static LegendLanguageTeacherProposalRequest ProposalRequest() =>
        new(
            "en",
            "ht",
            "Improve reusable conversational assistance requests.",
            [
                new LegendLanguageTeacherEvidence(
                    "evidence-1",
                    "I need your help.",
                    "Mwen bezwen èd ou.",
                    "FounderApproved",
                    "Validated"),
                new LegendLanguageTeacherEvidence(
                    "evidence-2",
                    "We need your help.",
                    "Nou bezwen èd ou.",
                    "ProviderDerived",
                    "SystemValidated")
            ],
            2,
            SemanticFamilyKey: "generated.request.assistance",
            SemanticCategory: "Requesting assistance");

    private static HttpResponseMessage StructuredResponse(
        object value) =>
        StructuredRawResponse(
            JsonSerializer.Serialize(value));

    private static HttpResponseMessage StructuredRawResponse(
        string structuredJson)
    {
        var envelope =
            JsonSerializer.Serialize(
                new
                {
                    status = "completed",
                    output = new[]
                    {
                        new
                        {
                            type = "message",
                            content = new[]
                            {
                                new
                                {
                                    type = "output_text",
                                    text = structuredJson
                                }
                            }
                        }
                    }
                });

        return new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content =
                new StringContent(
                    envelope,
                    Encoding.UTF8,
                    "application/json")
        };
    }

    private sealed class StubHttpClientFactory
        : IHttpClientFactory
    {
        private readonly StubHttpMessageHandler _handler;

        public StubHttpClientFactory(
            StubHttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            if (!string.Equals(
                    name,
                    "LegendLanguageTeacher",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected client name: {name}");
            }

            return new HttpClient(
                _handler,
                disposeHandler: false);
        }
    }

    private sealed class StubHttpMessageHandler
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        public void Enqueue(
            HttpResponseMessage response)
        {
            _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;

            LastAuthorizationScheme =
                request.Headers.Authorization?.Scheme;

            LastAuthorizationParameter =
                request.Headers.Authorization?.Parameter;

            LastBody =
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(
                        cancellationToken);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No fake response was queued.");
            }

            return _responses.Dequeue();
        }
    }
}
