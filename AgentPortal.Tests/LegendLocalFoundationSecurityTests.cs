using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendLocalFoundationSecurityTests
{
    private const string Model = "controlled-fixture-model";
    private const string Resource = "/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/fixture/providers/Microsoft.Compute/virtualMachines/controlled-serving";
    private static readonly string Revision = new('a', 40);
    private const string FounderId = "74620db7-16f5-48bc-9570-5a572d9d53ce";
    private static readonly string MacHostId = new('d', 64);

    [Fact]
    public async Task FounderMac_AuthenticatedFounderUsesPinnedTlsConnectorAndExactMacReceipt()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(MacResponse()))));
        var result = await Create(factory.Object, "https://founder-connector.example/v1/responses", MacConfiguration())
            .GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.IndependentAnswering) with
            { RequestingActorId = FounderId });
        Assert.True(result.Succeeded);
        Assert.Equal("Controlled answer.", result.Text);
        Assert.Equal("LegendControlled", result.Hosting);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FounderMac_BufferedAndStreamingShareIdentityAndPolicyValidation(bool stream)
    {
        var configuration = MacConfiguration();
        configuration["LegendConnect:Foundation:StreamResponses"] = stream.ToString();
        var receipt = MacResponse();
        receipt["stream"] = stream;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(stream ? Completed(receipt) : JsonSerializer.Serialize(receipt),
                mediaType: stream ? "text/event-stream" : "application/json")));
        var result = await Create(factory.Object, overrides: configuration).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.IndependentAnswering) with { RequestingActorId = FounderId });
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal("Controlled answer.", result.Text);
        Assert.Contains("stream=" + stream.ToString().ToLowerInvariant(), result.InferenceSettings);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task FounderMac_BufferedCannotAcceptAbsentOrMismatchedFramingReceipt(bool? stream)
    {
        var configuration = MacConfiguration();
        configuration["LegendConnect:Foundation:StreamResponses"] = "false";
        var receipt = MacResponse();
        if (stream.HasValue) receipt["stream"] = stream.Value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(JsonSerializer.Serialize(receipt), mediaType: "application/json")));
        var result = await Create(factory.Object, overrides: configuration).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = FounderId });
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_identity_or_response_invalid", result.ErrorCode);
        Assert.Null(result.Text);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FounderMac_BufferedRejectsOversizedResponseWithoutFallback()
    {
        var configuration = MacConfiguration();
        configuration["LegendConnect:Foundation:StreamResponses"] = "false";
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(new string(' ', 2_000_001), mediaType: "application/json")));
        var result = await Create(factory.Object, overrides: configuration).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = FounderId });
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_response_size_limit", result.ErrorCode);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FounderMac_BufferedByteLimitRejectsMultibytePayloadBelowCharacterLimit()
    {
        var configuration = MacConfiguration();
        configuration["LegendConnect:Foundation:StreamResponses"] = "false";
        var receipt = MacResponse();
        receipt["stream"] = false;
        receipt["padding"] = new string('é', 1_000_001);
        var body = JsonSerializer.Serialize(receipt, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Assert.True(body.Length < 2_000_000);
        Assert.True(Encoding.UTF8.GetByteCount(body) > 2_000_000);
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(body, mediaType: "application/json")));
        var result = await Create(factory.Object, overrides: configuration).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = FounderId });
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_response_size_limit", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Null(result.Output);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("founder@example.com")]
    [InlineData("1fc326e9-178d-414d-8780-fbbdf74392a8")]
    public async Task FounderMac_AbsentInvalidOrOtherActorCannotCreateAnyClient(string? actor)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object, overrides: MacConfiguration()).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = actor });
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_founder_required", result.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("founder@example.com")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task FounderMac_InvalidConfiguredFounderCannotAuthorizeMatchingText(string configuredFounder)
    {
        var configuration = MacConfiguration();
        configuration["FOUNDER_OID"] = configuredFounder;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object, overrides: configuration).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = configuredFounder });
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_founder_required", result.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("HostKind", "FounderMacTypo")]
    [InlineData("MacHostId", "unverified")]
    [InlineData("ApiKey", "")]
    [InlineData("Engine", "Vllm")]
    [InlineData("EngineVersion", "unknown")]
    [InlineData("EnableThinking", "true")]
    [InlineData("MaxContextTokens", "32768")]
    public async Task FounderMac_InvalidHostOrExecutionConfigurationFailsBeforeClient(string setting, string value)
    {
        var configuration = MacConfiguration();
        configuration["LegendConnect:Foundation:" + setting] = value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object, overrides: configuration).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = FounderId });
        Assert.False(result.Succeeded);
        Assert.StartsWith("local_foundation_", result.ErrorCode);
        Assert.Null(result.Text);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("http://founder-connector.example/v1/responses")]
    [InlineData("https://founder-connector.example:8443/v1/responses")]
    [InlineData("https://founder-connector.example/v1/responses?target=other")]
    [InlineData("https://secret@founder-connector.example/v1/responses")]
    [InlineData("https://founder-connector.example/v1/responses#other")]
    [InlineData("https://founder-connector.example/other")]
    public async Task FounderMac_AmbiguousOrUnencryptedConnectorFailsBeforeClient(string endpoint)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object, endpoint, MacConfiguration()).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = FounderId });
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_not_configured", result.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("host_kind", null)]
    [InlineData("host_kind", "AzureVm")]
    [InlineData("mac_host_id", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    [InlineData("host_verification", "azure-imds-resource-and-tag-v1")]
    public async Task FounderMac_WrongHostReceiptCannotClaimControlledAnswer(string field, string? value)
    {
        var receipt = MacResponse();
        if (value is null) receipt.Remove(field);
        else receipt[field] = value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object, overrides: MacConfiguration()).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly) with { RequestingActorId = FounderId });
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
        Assert.Equal("local_foundation_identity_or_response_invalid", result.ErrorCode);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeOnly_UsesOnlyControlledClientAndRequiresExactReceipt()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed())));
        var transport = Create(factory.Object);
        var result = await transport.GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.True(result.Succeeded);
        Assert.Equal("Controlled answer.", result.Text);
        Assert.Equal(Model, result.ModelVersion);
        Assert.Equal("LegendControlled", result.Hosting);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownModel_AbsentOrNativeOnlyPolicy_CreatesZeroClients(bool explicitNativeOnly)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object).GenerateAsync("unregistered-hosted-model",
            Request(explicitNativeOnly ? LegendConnectExternalProviderPolicy.NativeOnly : null));
        Assert.False(result.Succeeded);
        Assert.Equal("external_provider_forbidden_by_policy", result.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IndependentAnswering_UnknownHostedModelCreatesZeroClients()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object).GenerateAsync("unregistered-hosted-model",
            Request(LegendConnectExternalProviderPolicy.IndependentAnswering));
        Assert.False(result.Succeeded);
        Assert.Equal("external_provider_forbidden_by_policy", result.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("https://example.com/v1/responses")]
    [InlineData("https://8.8.8.8/v1/responses")]
    [InlineData("http://10.0.0.1/v1/responses")]
    [InlineData("http://127.0.0.1/v1/responses?redirect=https://example.com")]
    [InlineData("http://user:password@127.0.0.1/v1/responses")]
    public async Task PublicOrAmbiguousEndpoint_CannotBecomeLocalByConfigurationLabel(string endpoint)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var result = await Create(factory.Object, endpoint).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_not_configured", result.ErrorCode);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("model", "different-model")]
    [InlineData("model_revision", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("hosting", "ExternalHosted")]
    [InlineData("adapter_version", "unapproved-adapter")]
    [InlineData("azure_resource_id", "/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/fixture/providers/Microsoft.Compute/virtualMachines/another-host")]
    [InlineData("host_verification", "unverified")]
    public async Task MismatchedServingReceipt_IsRejectedWithoutExternalFallback(string field, string value)
    {
        var receipt = Response();
        receipt[field] = value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_identity_or_response_invalid", result.ErrorCode);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("data: []\n\n")]
    [InlineData("data: {\"type\":42}\n\n")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":[]}\n\n")]
    public async Task MalformedLocalEvents_FailClosedWithoutThrowingOrExternalFallback(string events)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(events)));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("[42]")]
    [InlineData("[{\"type\":42}]")]
    [InlineData("[{\"type\":\"message\",\"content\":[42]}]")]
    [InlineData("[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":42}]}]")]
    public async Task MalformedNestedOutput_CannotThrowOrProduceCompletedEvaluationText(string output)
    {
        var receipt = Response();
        using var parsed = JsonDocument.Parse(output);
        receipt["output"] = parsed.RootElement.Clone();
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.Null(result.Text);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    public async Task NonCompletedGeneration_CannotBecomeCompletedAnswerOrEvaluationProof(string status)
    {
        var receipt = Response();
        receipt["status"] = status;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        // A parsed incomplete envelope may continue in the conversation loop,
        // but must never expose completed text to model evaluation.
        Assert.Equal(status == "incomplete", result.Succeeded);
        Assert.Null(result.Text);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NonStringTerminalStatus_FailsAsMalformedDataWithoutThrowing()
    {
        var receipt = Response();
        receipt["status"] = 42;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMismatchedExecutionReceipt_CannotBecomeCompletedProof(bool mismatched)
    {
        var receipt = Response();
        if (mismatched)
            receipt["execution_limits"] = new { max_context_tokens = 8192, max_output_tokens = 1024,
                timeout_seconds = 60, maximum_concurrent_requests = 1 };
        else
            receipt.Remove("generation_settings");
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
        Assert.Equal(mismatched ? "local_foundation_execution_receipt_mismatch" : "local_foundation_execution_receipt_invalid", result.ErrorCode);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("DifferentEngine")]
    public async Task MissingOrMismatchedEngine_CannotClaimControlledExecution(string? engine)
    {
        var receipt = Response();
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(receipt["generation_settings"]))!;
        if (engine is null) settings.Remove("engine");
        else settings["engine"] = engine;
        receipt["generation_settings"] = settings;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(receipt))));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ContextLimitFailure_PreservesAllowlistedReasonWithoutExternalFallback()
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        clients.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(
                "data: {\"type\":\"response.failed\",\"error\":\"local_context_limit\"}\n\n")));
        var result = await Create(clients.Object).GenerateAsync(Model,
            Request(LegendConnectExternalProviderPolicy.IndependentAnswering));
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_context_limit", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Null(result.Output);
        clients.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChangedActualDestination_IsRejectedEvenIfReceiptClaimsLocal()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new ReceiptHandler(Completed(), "https://example.com/v1/responses")));
        var result = await Create(factory.Object).GenerateAsync(Model, Request(LegendConnectExternalProviderPolicy.NativeOnly));
        Assert.False(result.Succeeded);
        Assert.Equal("local_foundation_endpoint_mismatch", result.ErrorCode);
        factory.Verify(item => item.CreateClient("LegendLocalFoundation"), Times.Once);
        factory.VerifyNoOtherCalls();
    }

    private static LegendModelTaskRequest Request(LegendConnectExternalProviderPolicy? policy) =>
        new("conversation", "Follow the authenticated instructions.", "A held-out ordinary request.",
            "response", ProviderPolicy: policy);

    private static LegendConnectModelInferenceTransport Create(IHttpClientFactory factory,
        string endpoint = "http://127.0.0.1:8091/v1/responses", Dictionary<string, string?>? overrides = null) => new(factory,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Foundation:Enabled"] = "true",
            ["LegendConnect:Foundation:Model"] = Model,
            ["LegendConnect:Foundation:ModelRevision"] = Revision,
            ["LegendConnect:Foundation:Endpoint"] = endpoint,
            ["LegendConnect:Foundation:ApiKey"] = "controlled-fixture-key",
            ["LegendConnect:Foundation:AzureResourceId"] = Resource,
            ["LegendConnect:Foundation:Engine"] = "Vllm",
            ["LegendConnect:Foundation:EngineVersion"] = "0.0-fixture",
            ["LegendConnect:Foundation:ToolCallParser"] = "hermes",
            ["LegendConnect:Foundation:ReasoningParser"] = "qwen3",
            ["LegendConnect:Foundation:TimeoutSeconds"] = "60",
            // External credentials are deliberately present: policy, rather
            // than missing configuration, must prevent an external client.
            ["LegendConnect:ModelEvaluation:ApiKey"] = "external-fixture-key",
            ["LegendConnect:ModelEvaluation:Endpoint"] = "https://example.com/v1/responses"
        }).AddInMemoryCollection(overrides ?? new Dictionary<string, string?>()).Build(), NullLogger<LegendConnectModelInferenceTransport>.Instance);

    private static Dictionary<string, string?> MacConfiguration() => new()
    {
        ["FOUNDER_OID"] = FounderId,
        ["LegendConnect:Foundation:HostKind"] = "FounderMac",
        ["LegendConnect:Foundation:MacHostId"] = MacHostId,
        ["LegendConnect:Foundation:Engine"] = "Mlx",
        ["LegendConnect:Foundation:EngineVersion"] = "0.31.3",
        ["LegendConnect:Foundation:MaxContextTokens"] = "8192",
        ["LegendConnect:Foundation:MaxOutputTokens"] = "768"
    };

    private static Dictionary<string, object?> MacResponse()
    {
        var receipt = Response();
        receipt.Remove("azure_resource_id");
        receipt["host_kind"] = "FounderMac";
        receipt["mac_host_id"] = MacHostId;
        receipt["host_verification"] = "macos-arm64-user-bound-v1";
        receipt["execution_limits"] = new { max_context_tokens = 8192, max_output_tokens = 768,
            timeout_seconds = 60, maximum_concurrent_requests = 1 };
        var generation = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(receipt["generation_settings"]))!;
        generation["max_output_tokens"] = 768;
        generation["engine"] = "Mlx";
        generation["engine_version"] = "0.31.3";
        receipt["generation_settings"] = generation;
        return receipt;
    }

    private static Dictionary<string, object?> Response() => new()
    {
        ["model"] = Model,
        ["model_revision"] = Revision,
        ["adapter_version"] = "",
        ["hosting"] = "LegendControlled",
        ["azure_resource_id"] = Resource,
        ["host_verification"] = "azure-imds-resource-and-tag-v1",
        ["status"] = "completed",
        ["execution_limits"] = new { max_context_tokens = 32768, max_output_tokens = 1024,
            timeout_seconds = 60, maximum_concurrent_requests = 1 },
        ["generation_settings"] = new { max_output_tokens = 1024, temperature = 0,
            enable_thinking = false, chat_template_sha256 = new string('c', 64),
            engine = "Vllm", engine_version = "0.0-fixture", tool_call_parser = "hermes", reasoning_parser = "qwen3", top_p = 1, top_k = 0, seed = 73,
            reasoning_effort = (string?)null, preserve_thinking = false },
        ["output"] = new[] { new { type = "message", role = "assistant",
            content = new[] { new { type = "output_text", text = "Controlled answer." } } } }
    };

    private static string Completed(Dictionary<string, object?>? response = null) => "data: " +
        JsonSerializer.Serialize(new { type = "response.completed", response = response ?? Response() }) + "\n\n";

    private sealed class ReceiptHandler(string events, string? returnedUri = null, string mediaType = "text/event-stream") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(request.Content);
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.InRange(bytes.Length, 1, 2_000_000);
            Assert.Equal((long)bytes.Length, request.Content.Headers.ContentLength);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("controlled-fixture-key", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = returnedUri is null ? request : new HttpRequestMessage(HttpMethod.Post, returnedUri),
                Content = new StringContent(events, Encoding.UTF8, mediaType)
            };
        }
    }
}
