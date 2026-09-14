using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

/// <summary>Authenticated remote compute adapter for the existing DB-owned
/// lifecycle. Corpus bytes are streamed to the controlled worker; this client
/// writes no dataset, model, job or checkpoint store on the app host.</summary>
internal sealed class ControlledLegendConnectModelTrainingBackend(
    IConfiguration configuration, IHttpClientFactory clients) : ILegendConnectModelTrainingBackend
{
    private const string Prefix = "LegendConnect:ModelTraining:";
    public string TrainingProvider => LegendConnectModelTrainingConfiguration.ResolveBackend(configuration) switch
    {
        "ControlledTransformers" => "ControlledTransformers",
        "ControlledMlx" => "ControlledMlx",
        _ => throw new InvalidOperationException("controlled_training_backend_invalid")
    };
    private bool IsMac => configuration["LegendConnect:Foundation:HostKind"] == "FounderMac";
    private bool HostMatchesBackend() => LegendConnectModelInferenceTransport.IsControlledFoundationHost(configuration) &&
        TrainingProvider == (IsMac ? "ControlledMlx" : "ControlledTransformers");

    private string ConfigurationJson()
    {
        var model = configuration[Prefix + "BaseModel"];
        var revision = configuration["LegendConnect:Foundation:ModelRevision"];
        var trainer = configuration[Prefix + "TrainerCodeSha256"];
        var resourceId = configuration["LegendConnect:Foundation:AzureResourceId"];
        if (string.IsNullOrWhiteSpace(model) || model != configuration["LegendConnect:Foundation:Model"] ||
            !Hex(revision, 40) || !Hex(trainer, 64) || !HostMatchesBackend()) throw new InvalidOperationException("controlled_training_configuration_invalid");
        if (IsMac)
            return JsonSerializer.Serialize(new
            {
                schema = "controlled-mlx-training-v1", base_model = model, base_revision = revision,
                host_kind = "FounderMac", mac_host_id = configuration["LegendConnect:Foundation:MacHostId"],
                founder_id = LegendConnectModelInferenceTransport.ResolveConfiguredFounderObjectId(configuration),
                trainer_sha256 = trainer, iterations = Integer("Iterations", 100, 1, 2000),
                learning_rate = Rate(), lora_rank = Integer("LoraRank", 8, 1, 64),
                max_sequence_tokens = Integer("MaxSequenceTokens", 512, 64, 2048),
                deadline_seconds = Integer("DeadlineSeconds", 600, 30, 3600), seed = Integer("Seed", 73, 0, 1000000)
            });
        return JsonSerializer.Serialize(new
        {
            schema = "controlled-transformers-training-v1", base_model = model, base_revision = revision,
            azure_resource_id = resourceId,
            trainer_sha256 = trainer, iterations = Integer("Iterations", 100, 1, 2000),
            learning_rate = Rate(), lora_rank = Integer("LoraRank", 8, 1, 64),
            max_sequence_tokens = Integer("MaxSequenceTokens", 512, 64, 8192),
            deadline_seconds = Integer("DeadlineSeconds", 600, 30, 3600), seed = Integer("Seed", 73, 0, 1000000)
        });
    }

    public Task<string> GetTrainingConfigurationIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hash(Encoding.UTF8.GetBytes(ConfigurationJson())));
    }

    public async Task<LegendModelTrainingUploadResult> UploadTrainingFileAsync(string fileName, byte[] jsonl,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>(Prefix + "Enabled")) return new(false, null, "controlled_training_disabled", false);
        if (jsonl.Length is 0 or > 200000000) return new(false, null, "controlled_training_dataset_invalid", false);
        var identity = Hash(jsonl);
        using var content = new ByteArrayContent(jsonl);
        content.Headers.ContentType = new("application/x-ndjson");
        var response = await SendAsync(HttpMethod.Put, "files/" + identity, content, cancellationToken);
        if (!response.Succeeded) return new(false, null, response.ErrorCode, response.Retryable);
        try
        {
            using var parsed = JsonDocument.Parse(response.Json!);
            if (parsed.RootElement.GetProperty("file_id").GetString() != identity ||
                parsed.RootElement.GetProperty("bytes").GetInt64() != jsonl.Length)
                return new(false, null, "controlled_training_upload_receipt_mismatch", false);
            return new(true, identity, null, false);
        }
        catch (Exception exception) when (Malformed(exception))
        { return new(false, null, "controlled_training_upload_receipt_invalid", false); }
    }

    public async Task<LegendModelTrainingJobResult> CreateTrainingJobAsync(string trainingFileId, string baseModel,
        string runKey, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>(Prefix + "Enabled")) return Failure("controlled_training_disabled");
        if (!Hex(trainingFileId, 64) || !Hex(runKey, 64) || baseModel != configuration[Prefix + "BaseModel"])
            return Failure("controlled_training_job_invalid");
        var settings = ConfigurationJson();
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            dataset_sha256 = trainingFileId, configuration_identity = Hash(Encoding.UTF8.GetBytes(settings)), configuration_json = settings
        }), Encoding.UTF8, "application/json");
        var response = await SendAsync(HttpMethod.Put, "jobs/" + runKey, content, cancellationToken);
        return await ParseJobAsync(runKey, response, cancellationToken);
    }

    public async Task<LegendModelTrainingJobLookupResult> FindTrainingJobByRunKeyAsync(string runKey,
        CancellationToken cancellationToken = default)
    {
        if (!Hex(runKey, 64)) return new(LegendModelTrainingJobLookupState.Indeterminate, null, null, null, "controlled_training_job_invalid", false);
        var response = await SendAsync(HttpMethod.Get, "jobs/" + runKey, null, cancellationToken);
        if (response.Status == HttpStatusCode.NotFound)
            return new(LegendModelTrainingJobLookupState.NotFound, null, null, null, null, false);
        var result = await ParseJobAsync(runKey, response, cancellationToken);
        return new(result.Succeeded ? LegendModelTrainingJobLookupState.Found : LegendModelTrainingJobLookupState.Indeterminate,
            result.JobId, result.Status, result.ChallengerModelVersion, result.ErrorCode, result.Retryable);
    }

    public async Task<LegendModelTrainingJobResult> GetTrainingJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!Hex(jobId, 64)) return Failure("controlled_training_job_invalid");
        return await ParseJobAsync(jobId, await SendAsync(HttpMethod.Get, "jobs/" + jobId, null, cancellationToken), cancellationToken);
    }

    public async Task<LegendConnectTrainingCheckpoint?> GetCheckpointAsync(string runKey, CancellationToken cancellationToken = default)
    {
        if (!Hex(runKey, 64)) return null;
        var response = await SendAsync(HttpMethod.Get, "jobs/" + runKey + "/checkpoint", null, cancellationToken);
        if (!response.Succeeded) return null;
        return LegendConnectTrainingCheckpointAuthority.ReadControlledReceipt(configuration, runKey, response.Json!);
    }

    private async Task<LegendModelTrainingJobResult> ParseJobAsync(string runKey, RemoteResponse response, CancellationToken cancellationToken)
    {
        if (!response.Succeeded) return new(false, null, null, null, response.ErrorCode, response.Retryable);
        try
        {
            using var document = JsonDocument.Parse(response.Json!);
            var root = document.RootElement;
            if (root.GetProperty("run_key").GetString() != runKey ||
                root.GetProperty("configuration_identity").GetString() != await GetTrainingConfigurationIdentityAsync(cancellationToken))
                return Failure("controlled_training_job_receipt_mismatch");
            var state = root.GetProperty("status").GetString();
            if (state is not ("queued" or "running" or "succeeded" or "failed" or "cancelled"))
                return Failure("controlled_training_job_state_invalid");
            if (state == "succeeded")
            {
                var checkpoint = await GetCheckpointAsync(runKey, cancellationToken);
                return checkpoint is null ? Failure("controlled_training_checkpoint_unverified") :
                    new(true, runKey, state, checkpoint.ModelVersion, null, false);
            }
            return new(true, runKey, state, null, state is "failed" or "cancelled" ? "controlled_training_compute_failed" : null, false);
        }
        catch (Exception exception) when (Malformed(exception)) { return Failure("controlled_training_job_receipt_invalid"); }
    }

    private async Task<RemoteResponse> SendAsync(HttpMethod method, string route, HttpContent? content, CancellationToken cancellationToken)
    {
        var key = configuration["LegendConnect:Foundation:ApiKey"];
        if (!HostMatchesBackend() ||
            string.IsNullOrWhiteSpace(key) || !Uri.TryCreate(configuration["LegendConnect:Foundation:Endpoint"], UriKind.Absolute, out var endpoint) ||
            !LegendConnectModelInferenceTransport.IsControlledFoundationEndpoint(endpoint, authenticated: true, configuration["LegendConnect:Foundation:HostKind"]))
            return new(false, null, null, "controlled_training_endpoint_unconfigured", false);
        var address = new UriBuilder(endpoint) { Path = "/v1/training/" + route }.Uri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, address) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await clients.CreateClient("LegendLocalFoundation").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, response.StatusCode, null, "controlled_training_http_" + (int)response.StatusCode,
                    response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode == 429 || (int)response.StatusCode >= 500);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(bytes, deadline.Token)) > 0)
            {
                if (buffer.Length + count > 1000000) return new(false, response.StatusCode, null, "controlled_training_receipt_too_large", false);
                buffer.Write(bytes, 0, count);
            }
            return new(true, response.StatusCode, Encoding.UTF8.GetString(buffer.ToArray()), null, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, null, null, "controlled_training_request_deadline_exceeded", true); }
        catch (HttpRequestException) { return new(false, null, null, "controlled_training_transport_failed", true); }
    }

    private int Integer(string name, int fallback, int minimum, int maximum)
    {
        var raw = configuration[Prefix + name];
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException("controlled_training_resource_limits_invalid");
        return value;
    }
    private decimal Rate()
    {
        var raw = configuration[Prefix + "LearningRate"];
        if (raw is null) return 0.00005m;
        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            value is < 0.000001m or > 0.001m) throw new InvalidOperationException("controlled_training_learning_rate_invalid");
        return value;
    }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Malformed(Exception exception) => exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException;
    private static LegendModelTrainingJobResult Failure(string code) => new(false, null, null, null, code, false);
    private sealed record RemoteResponse(bool Succeeded, HttpStatusCode? Status, string? Json, string? ErrorCode, bool Retryable);
}
