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
        string endpoint = "http://127.0.0.1:8091/v1/responses") => new(factory,
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
        }).Build(), NullLogger<LegendConnectModelInferenceTransport>.Instance);

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

    private sealed class ReceiptHandler(string events, string? returnedUri = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(request.Content);
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.InRange(bytes.Length, 1, 2_000_000);
            Assert.Equal((long)bytes.Length, request.Content.Headers.ContentLength);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = returnedUri is null ? request : new HttpRequestMessage(HttpMethod.Post, returnedUri),
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
