using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectControlledModelTrainingTests
{
    [Fact]
    public async Task ConfigurationIdentityBindsBaseTrainerSettingsAndRejectsInvalidJobWithoutRequests()
    {
        var settings = Configuration();
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var backend = new ControlledLegendConnectModelTrainingBackend(settings, clients.Object);
        var original = await backend.GetTrainingConfigurationIdentityAsync();
        Assert.Equal(original, await backend.GetTrainingConfigurationIdentityAsync());
        settings["LegendConnect:ModelTraining:LearningRate"] = "0.00006";
        var changed = await backend.GetTrainingConfigurationIdentityAsync();
        Assert.NotEqual(original, changed);
        settings["LegendConnect:ModelTraining:TrainerCodeSha256"] = new string('f', 64);
        Assert.NotEqual(changed, await backend.GetTrainingConfigurationIdentityAsync());
        Assert.Equal("controlled_training_job_invalid", (await backend.GetTrainingJobAsync("../../job")).ErrorCode);
        settings["LegendConnect:ModelTraining:LearningRate"] = "NaN";
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetTrainingConfigurationIdentityAsync());
        Assert.Throws<InvalidOperationException>(() => LegendConnectModelTrainingConfiguration.ResolveBackend(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["LegendConnect:ModelTraining:Backend"] = "Typo" }).Build()));
        Assert.Equal("ControlledTransformers", LegendConnectModelTrainingConfiguration.ResolveBackend(null));
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadRejectsChangedReceiptWithoutRetryOrLocalPersistence()
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var calls = 0;
        clients.Setup(factory => factory.CreateClient("LegendLocalFoundation")).Returns(new HttpClient(new Handler(request =>
        {
            calls++;
            return Task.FromResult(Json(new { file_id = new string('0', 64), bytes = 3 }));
        })));
        var result = await new ControlledLegendConnectModelTrainingBackend(Configuration(), clients.Object)
            .UploadTrainingFileAsync("fixture", [1, 2, 3]);
        Assert.False(result.Succeeded);
        Assert.Equal("controlled_training_upload_receipt_mismatch", result.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CheckpointPreservesTrainingOriginAcrossServingHostMoveAndRejectsRecipeTampering()
    {
        var settings = Configuration();
        var origin = settings["LegendConnect:Foundation:AzureResourceId"]!;
        var serving = origin.Replace("controlled-fixture", "serving-fixture", StringComparison.Ordinal);
        settings["LegendConnect:Foundation:AzureResourceId"] = serving;
        var run = new string('c', 64);
        var recipe = JsonSerializer.Serialize(new
        {
            schema = "controlled-transformers-training-v1", base_model = "controlled-base", base_revision = new string('a', 40),
            azure_resource_id = origin, trainer_sha256 = new string('b', 64)
        });
        var configurationIdentity = Hash(Encoding.UTF8.GetBytes(recipe));
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model_version = "controlled:" + run, adapter_format = "peft", base_repository = "controlled-base", base_model_revision = new string('a', 40),
            azure_resource_id = origin, dataset_sha256 = new string('d', 64), trainer_sha256 = new string('b', 64),
            training_configuration_identity = configurationIdentity, base_manifest_sha256 = new string('e', 64),
            adapter_sha256 = new string('f', 64), adapter_config_sha256 = new string('a', 64)
        });
        string Receipt(string recipeText, bool macHost = false) => JsonSerializer.Serialize(new
        {
            status = "succeeded", run_key = run, model_version = "controlled:" + run, weights_updated = true, usable_checkpoint = true,
            dataset_sha256 = new string('d', 64), configuration_identity = configurationIdentity,
            configuration_json = recipeText, checkpoint_manifest_base64 = Convert.ToBase64String(manifestBytes),
            checkpoint_manifest_sha256 = Hash(manifestBytes),
            host_receipt = macHost
                ? (object)new { host_kind = "FounderMac", mac_host_id = new string('2', 64), host_verification = "macos-arm64-user-bound-v1" }
                : new { azure_resource_id = serving, azure_vm_id = "22222222-2222-2222-2222-222222222222", host_verification = "azure-imds-resource-and-tag-v1" }
        });
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(MacConfiguration(), run, Receipt(recipe, macHost: true)));
        var valid = LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt(recipe));
        Assert.NotNull(valid);
        Assert.Equal(Hash(manifestBytes), valid.AdapterVersion);
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt(recipe.Replace(origin, serving, StringComparison.Ordinal))));
        settings["LegendConnect:Foundation:AzureResourceId"] = origin;
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt(recipe)));
    }

    [Fact]
    public async Task DisabledTrainingAndPublicEndpointMakeZeroRequests()
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var settings = Configuration();
        settings["LegendConnect:ModelTraining:Enabled"] = "false";
        var backend = new ControlledLegendConnectModelTrainingBackend(settings, clients.Object);
        var disabled = await backend.UploadTrainingFileAsync("fixture", [123]);
        Assert.Equal("controlled_training_disabled", disabled.ErrorCode);
        settings["LegendConnect:ModelTraining:Enabled"] = "true";
        settings["LegendConnect:Foundation:Endpoint"] = "https://public.example/v1/responses";
        var rejected = await backend.UploadTrainingFileAsync("fixture", [123]);
        Assert.Equal("controlled_training_endpoint_unconfigured", rejected.ErrorCode);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadAndJobBindExactBytesSettingsAndAuthenticatedControlledClient()
    {
        var settings = Configuration();
        var calls = 0;
        var bytes = Encoding.UTF8.GetBytes("fixture transport bytes only");
        var dataset = Hash(bytes);
        var run = new string('c', 64);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        clients.Setup(factory => factory.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new Handler(async request =>
            {
                calls++;
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("nonsecret-fixture-key", request.Headers.Authorization?.Parameter);
                if (calls == 1)
                {
                    Assert.Equal("/v1/training/files/" + dataset, request.RequestUri!.AbsolutePath);
                    Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                    return Json(new { file_id = dataset, bytes = bytes.Length });
                }
                Assert.Equal("/v1/training/jobs/" + run, request.RequestUri!.AbsolutePath);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var root = document.RootElement;
                Assert.Equal(dataset, root.GetProperty("dataset_sha256").GetString());
                var configurationJson = root.GetProperty("configuration_json").GetString()!;
                var identity = Hash(Encoding.UTF8.GetBytes(configurationJson));
                Assert.Equal(identity, root.GetProperty("configuration_identity").GetString());
                using var configuration = JsonDocument.Parse(configurationJson);
                Assert.Equal("controlled-base", configuration.RootElement.GetProperty("base_model").GetString());
                Assert.Equal(100, configuration.RootElement.GetProperty("iterations").GetInt32());
                return Json(new { run_key = run, status = "running", configuration_identity = identity });
            })));
        var backend = new ControlledLegendConnectModelTrainingBackend(settings, clients.Object);
        var upload = await backend.UploadTrainingFileAsync("ignored-display-name", bytes);
        Assert.True(upload.Succeeded);
        Assert.Equal(dataset, upload.FileId);
        var job = await backend.CreateTrainingJobAsync(dataset, "controlled-base", run);
        Assert.True(job.Succeeded);
        Assert.Equal("running", job.Status);
        Assert.Null(job.ChallengerModelVersion);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ClaimedSuccessWithoutVerifiedCheckpointCannotCompleteTraining()
    {
        var settings = Configuration();
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var backend = new ControlledLegendConnectModelTrainingBackend(settings, clients.Object);
        var identity = await backend.GetTrainingConfigurationIdentityAsync();
        var run = new string('c', 64);
        var calls = 0;
        clients.Setup(factory => factory.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new Handler(request =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? Json(new { run_key = run, status = "succeeded", configuration_identity = identity })
                    : Json(new { status = "succeeded", run_key = run, model_version = "controlled:" + run,
                        weights_updated = false, usable_checkpoint = false }));
            })));
        var result = await backend.GetTrainingJobAsync(run);
        Assert.False(result.Succeeded);
        Assert.Equal("controlled_training_checkpoint_unverified", result.ErrorCode);
        Assert.Null(result.ChallengerModelVersion);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationDoesNotRetryOrCallAnotherProvider()
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var calls = 0;
        clients.Setup(factory => factory.CreateClient("LegendLocalFoundation"))
            .Returns(new HttpClient(new CancellableHandler(() => calls++)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var backend = new ControlledLegendConnectModelTrainingBackend(Configuration(), clients.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.GetTrainingJobAsync(new string('c', 64), cancellation.Token));
        Assert.Equal(1, calls);
        clients.Verify(factory => factory.CreateClient("LegendLocalFoundation"), Times.Once);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FounderMacRequiresExplicitMatchingBackendAndBoundsAndBindsCanonicalOwner()
    {
        var settings = MacConfiguration();
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var backend = new ControlledLegendConnectModelTrainingBackend(settings, clients.Object);
        Assert.Equal("ControlledMlx", backend.TrainingProvider);
        var identity = await backend.GetTrainingConfigurationIdentityAsync();
        settings["Founder:Oid"] = "33333333-3333-3333-3333-333333333333";
        Assert.Equal(identity, await backend.GetTrainingConfigurationIdentityAsync()); // FOUNDER_OID is the authority.
        settings["FOUNDER_OID"] = "44444444-4444-4444-4444-444444444444";
        Assert.NotEqual(identity, await backend.GetTrainingConfigurationIdentityAsync());
        settings["LegendConnect:ModelTraining:MaxSequenceTokens"] = "2049";
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetTrainingConfigurationIdentityAsync());
        settings["LegendConnect:ModelTraining:MaxSequenceTokens"] = "512";
        settings["LegendConnect:ModelTraining:Backend"] = "ControlledTransformers";
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetTrainingConfigurationIdentityAsync());
        Assert.False((await backend.UploadTrainingFileAsync("unusable", [1])).Succeeded);
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public void FounderMacCheckpointRequiresNewSchemaOwnerAndCurrentHostReceipt()
    {
        var settings = MacConfiguration();
        var originalHost = new string('1', 64);
        var currentHost = settings["LegendConnect:Foundation:MacHostId"]!;
        var run = new string('c', 64);
        var founder = settings["FOUNDER_OID"]!;
        string Receipt(string schema = "controlled-mlx-training-v1", string? owner = null, string format = "mlx")
        {
            var recipe = JsonSerializer.Serialize(new { schema, base_model = "controlled-base", base_revision = new string('a', 40),
                host_kind = "FounderMac", mac_host_id = originalHost, founder_id = owner ?? founder, trainer_sha256 = new string('b', 64) });
            var identity = Hash(Encoding.UTF8.GetBytes(recipe));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { model_version = "controlled:" + run, adapter_format = format,
                host_kind = "FounderMac", mac_host_id = originalHost, founder_id = owner ?? founder,
                base_repository = "controlled-base", base_model_revision = new string('a', 40),
                dataset_sha256 = new string('d', 64), trainer_sha256 = new string('b', 64), training_configuration_identity = identity,
                base_manifest_sha256 = new string('e', 64), adapter_sha256 = new string('f', 64), adapter_config_sha256 = new string('a', 64) });
            return JsonSerializer.Serialize(new { status = "succeeded", run_key = run, model_version = "controlled:" + run,
                weights_updated = true, usable_checkpoint = true, dataset_sha256 = new string('d', 64), configuration_identity = identity,
                configuration_json = recipe, checkpoint_manifest_base64 = Convert.ToBase64String(manifest), checkpoint_manifest_sha256 = Hash(manifest),
                host_receipt = new { host_kind = "FounderMac", mac_host_id = currentHost, host_verification = "macos-arm64-user-bound-v1" } });
        }
        Assert.NotNull(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt()));
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt(schema: "local-mlx-v1")));
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt(owner: "33333333-3333-3333-3333-333333333333")));
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt(format: "peft")));
        settings["LegendConnect:Foundation:MacHostId"] = originalHost;
        Assert.Null(LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(settings, run, Receipt()));
    }

    private static IConfigurationRoot MacConfiguration()
    {
        var configuration = Configuration();
        configuration["LegendConnect:ModelTraining:Backend"] = "ControlledMlx";
        configuration["LegendConnect:Foundation:HostKind"] = "FounderMac";
        configuration["LegendConnect:Foundation:Engine"] = "Mlx";
        configuration["LegendConnect:Foundation:MacHostId"] = new string('2', 64);
        configuration["FOUNDER_OID"] = "22222222-2222-2222-2222-222222222222";
        return configuration;
    }

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["LegendConnect:ModelTraining:Backend"] = "ControlledTransformers", ["LegendConnect:ModelTraining:Enabled"] = "true",
        ["LegendConnect:ModelTraining:BaseModel"] = "controlled-base", ["LegendConnect:ModelTraining:TrainerCodeSha256"] = new string('b', 64),
        ["LegendConnect:Foundation:Engine"] = "Vllm",
        ["LegendConnect:Foundation:Endpoint"] = "http://127.0.0.1:8111/v1/responses", ["LegendConnect:Foundation:ApiKey"] = "nonsecret-fixture-key",
        ["LegendConnect:Foundation:AzureResourceId"] = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/Microsoft.Compute/virtualMachines/controlled-fixture",
        ["LegendConnect:Foundation:Model"] = "controlled-base", ["LegendConnect:Foundation:ModelRevision"] = new string('a', 40)
    }).Build();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
    private sealed class CancellableHandler(Action called) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { called(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("Unreachable"); }
    }
}
