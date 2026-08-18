using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed record LegendModelTrainingUploadResult(
    bool Succeeded,
    string? FileId,
    string? ErrorCode,
    bool Retryable);

internal sealed record LegendModelTrainingJobResult(
    bool Succeeded,
    string? JobId,
    string? Status,
    string? ChallengerModelVersion,
    string? ErrorCode,
    bool Retryable);

internal interface ILegendConnectModelTrainingBackend
{
    Task<LegendModelTrainingUploadResult> UploadTrainingFileAsync(
        string fileName,
        byte[] jsonl,
        CancellationToken cancellationToken = default);

    Task<LegendModelTrainingJobResult> CreateTrainingJobAsync(
        string trainingFileId,
        string baseModel,
        string runKey,
        CancellationToken cancellationToken = default);

    Task<LegendModelTrainingJobResult> GetTrainingJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);
}

internal sealed class OpenAiLegendConnectModelTrainingBackend
    : ILegendConnectModelTrainingBackend
{
    private const string ClientName = "LegendModelTraining";
    private const string Prefix = "LegendConnect:ModelTraining:";
    private const string DefaultFilesEndpoint =
        "https://api.openai.com/v1/files";
    private const string DefaultJobsEndpoint =
        "https://api.openai.com/v1/fine_tuning/jobs";

    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiLegendConnectModelTrainingBackend> _logger;

    public OpenAiLegendConnectModelTrainingBackend(
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<OpenAiLegendConnectModelTrainingBackend> logger)
    {
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LegendModelTrainingUploadResult> UploadTrainingFileAsync(
        string fileName,
        byte[] jsonl,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetProviderConfiguration(
                out var key,
                out var filesEndpoint,
                out _))
        {
            return new(
                false,
                null,
                "model_training_provider_unavailable",
                false);
        }

        if (jsonl.Length == 0 ||
            jsonl.Length > 200_000_000 ||
            string.IsNullOrWhiteSpace(fileName))
        {
            return new(
                false,
                null,
                "model_training_invalid_file",
                false);
        }

        try
        {
            using var content = new MultipartFormDataContent();

            content.Add(
                new StringContent("fine-tune"),
                "purpose");

            var bytes = new ByteArrayContent(jsonl);
            bytes.Headers.ContentType =
                new MediaTypeHeaderValue("application/jsonl");

            content.Add(
                bytes,
                "file",
                fileName);

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    filesEndpoint)
                {
                    Content = content
                };

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);

            using var response =
                await _clients
                    .CreateClient(ClientName)
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    false,
                    null,
                    "model_training_file_upload_failed",
                    IsRetryable(response.StatusCode));
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);

            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty(
                    "id",
                    out var idElement) ||
                string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                return new(
                    false,
                    null,
                    "model_training_invalid_file_response",
                    false);
            }

            return new(
                true,
                idElement.GetString(),
                null,
                false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                false,
                null,
                "model_training_provider_timeout",
                true);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND model-training file upload failed.");

            return new(
                false,
                null,
                "model_training_provider_failed",
                true);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND model-training file response was invalid.");

            return new(
                false,
                null,
                "model_training_invalid_file_response",
                false);
        }
    }

    public async Task<LegendModelTrainingJobResult> CreateTrainingJobAsync(
        string trainingFileId,
        string baseModel,
        string runKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetProviderConfiguration(
                out var key,
                out _,
                out var jobsEndpoint) ||
            string.IsNullOrWhiteSpace(trainingFileId) ||
            string.IsNullOrWhiteSpace(baseModel))
        {
            return new(
                false,
                null,
                null,
                null,
                "model_training_provider_unavailable",
                false);
        }

        try
        {
            var payload = new
            {
                training_file = trainingFileId,
                model = baseModel,
                metadata = new Dictionary<string, string>
                {
                    ["legend_run_key"] = runKey
                }
            };

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    jobsEndpoint)
                {
                    Content = JsonContent.Create(payload)
                };

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);

            using var response =
                await _clients
                    .CreateClient(ClientName)
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    false,
                    null,
                    null,
                    null,
                    "model_training_job_create_failed",
                    IsRetryable(response.StatusCode));
            }

            return await ParseJobAsync(
                response,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                false,
                null,
                null,
                null,
                "model_training_provider_timeout",
                true);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND model-training job creation failed.");

            return new(
                false,
                null,
                null,
                null,
                "model_training_provider_failed",
                true);
        }
    }

    public async Task<LegendModelTrainingJobResult> GetTrainingJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetProviderConfiguration(
                out var key,
                out _,
                out var jobsEndpoint) ||
            string.IsNullOrWhiteSpace(jobId))
        {
            return new(
                false,
                null,
                null,
                null,
                "model_training_provider_unavailable",
                false);
        }

        try
        {
            var endpoint =
                new Uri(
                    jobsEndpoint.ToString().TrimEnd('/') +
                    "/" +
                    Uri.EscapeDataString(jobId));

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    endpoint);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    key);

            using var response =
                await _clients
                    .CreateClient(ClientName)
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    false,
                    jobId,
                    null,
                    null,
                    "model_training_job_read_failed",
                    IsRetryable(response.StatusCode));
            }

            return await ParseJobAsync(
                response,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                false,
                jobId,
                null,
                null,
                "model_training_provider_timeout",
                true);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "LEGEND model-training job read failed.");

            return new(
                false,
                jobId,
                null,
                null,
                "model_training_provider_failed",
                true);
        }
    }

    private static async Task<LegendModelTrainingJobResult> ParseJobAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

        var root = document.RootElement;

        var id =
            root.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

        var status =
            root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

        var challenger =
            root.TryGetProperty(
                    "fine_tuned_model",
                    out var modelElement) &&
                modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(status))
        {
            return new(
                false,
                id,
                status,
                challenger,
                "model_training_invalid_job_response",
                false);
        }

        return new(
            true,
            id,
            status,
            challenger,
            null,
            false);
    }

    private bool TryGetProviderConfiguration(
        out string key,
        out Uri filesEndpoint,
        out Uri jobsEndpoint)
    {
        key =
            (_configuration[Prefix + "ApiKey"] ??
             Environment.GetEnvironmentVariable(
                 "OPENAI_API_KEY") ??
             string.Empty)
            .Trim();

        var files =
            (_configuration[Prefix + "FilesEndpoint"] ??
             DefaultFilesEndpoint)
            .Trim();

        var jobs =
            (_configuration[Prefix + "JobsEndpoint"] ??
             DefaultJobsEndpoint)
            .Trim();

        if (string.IsNullOrWhiteSpace(key) ||
            !Uri.TryCreate(
                files,
                UriKind.Absolute,
                out var parsedFiles) ||
            !Uri.TryCreate(
                jobs,
                UriKind.Absolute,
                out var parsedJobs) ||
            parsedFiles.Scheme != Uri.UriSchemeHttps ||
            parsedJobs.Scheme != Uri.UriSchemeHttps)
        {
            filesEndpoint = default!;
            jobsEndpoint = default!;
            return false;
        }

        filesEndpoint = parsedFiles;
        jobsEndpoint = parsedJobs;
        return true;
    }

    private static bool IsRetryable(
        System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.RequestTimeout ||
        (int)status == 429 ||
        (int)status >= 500;
}

internal static class LegendConnectModelLifecycleLease
{
    internal static async Task<bool> TryClaimAsync(
        MasterAppDbContext db,
        Guid runId,
        DateTime now,
        System.Linq.Expressions.Expression<Func<LegendConnectModelTrainingRun, bool>> eligibility,
        CancellationToken cancellationToken)
    {
        var leaseExpiresUtc =
            now.AddMinutes(10);

        var query =
            db.Set<LegendConnectModelTrainingRun>()
                .Where(item =>
                    item.Id == runId &&
                    (item.LeaseExpiresUtc == null ||
                     item.LeaseExpiresUtc < now))
                .Where(eligibility);

        if (db.Database.IsRelational())
        {
            var claimed =
                await query.ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            item => item.LeaseExpiresUtc,
                            leaseExpiresUtc)
                        .SetProperty(
                            item => item.UpdatedUtc,
                            now),
                    cancellationToken);

            return claimed == 1;
        }

        var tracked =
            await query.SingleOrDefaultAsync(
                cancellationToken);

        if (tracked is null)
            return false;

        tracked.LeaseExpiresUtc =
            leaseExpiresUtc;

        tracked.UpdatedUtc =
            now;

        await db.SaveChangesAsync(
            cancellationToken);

        return true;
    }
}

internal sealed class LegendConnectModelTrainingService
{
    private const string Prefix = "LegendConnect:ModelTraining:";
    private const string Provider = "OpenAI";
    private const int DefaultMaximumAttempts = 4;

    private readonly MasterAppDbContext _db;
    private readonly LegendConnectTrainingDatasetCompiler _compiler;
    private readonly ILegendConnectModelTrainingBackend _backend;
    private readonly IConfiguration _configuration;

    internal LegendConnectModelTrainingService(
        MasterAppDbContext db,
        LegendConnectTrainingDatasetCompiler compiler,
        ILegendConnectModelTrainingBackend backend,
        IConfiguration configuration)
    {
        _db = db;
        _compiler = compiler;
        _backend = backend;
        _configuration = configuration;
    }

    internal async Task ProcessOneAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Enabled())
            return;

        var baseModel =
            (_configuration[Prefix + "BaseModel"] ?? string.Empty)
            .Trim();

        if (string.IsNullOrWhiteSpace(baseModel) ||
            baseModel.Length > 160)
        {
            return;
        }

        var manifest =
            await _compiler.CompileAsync(
                "Global",
                cancellationToken);

        if (manifest.Training.Count == 0)
            return;

        var run =
            await GetOrCreateRunAsync(
                manifest,
                baseModel,
                cancellationToken);

        if (run is null ||
            run.State is "TrainingCompleted" or "Failed")
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (!await LegendConnectModelLifecycleLease.TryClaimAsync(
                _db,
                run.Id,
                now,
                item =>
                    item.State != "TrainingCompleted" &&
                    item.State != "Failed",
                cancellationToken))
        {
            return;
        }

        run =
            await _db.Set<LegendConnectModelTrainingRun>()
                .SingleAsync(
                    item => item.Id == run.Id,
                    cancellationToken);

        if (run.TrainingFileId is null)
        {
            var jsonl =
                BuildTrainingJsonl(manifest);

            var upload =
                await _backend.UploadTrainingFileAsync(
                    $"legend-{manifest.DatasetIdentity[..12]}.jsonl",
                    jsonl,
                    cancellationToken);

            if (!upload.Succeeded ||
                string.IsNullOrWhiteSpace(upload.FileId))
            {
                await RecordFailureAsync(
                    run,
                    upload.ErrorCode ??
                        "model_training_file_upload_failed",
                    upload.Retryable,
                    cancellationToken);
                return;
            }

            run.TrainingFileId = upload.FileId;
            run.State = "TrainingFileReady";
            run.FailureCode = null;
            run.FailureDetail = null;
            run.LeaseExpiresUtc = null;
            run.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(
                cancellationToken);
            return;
        }

        if (run.ExternalJobId is null)
        {
            var created =
                await _backend.CreateTrainingJobAsync(
                    run.TrainingFileId,
                    run.BaseModel,
                    run.RunKey,
                    cancellationToken);

            if (!created.Succeeded ||
                string.IsNullOrWhiteSpace(created.JobId))
            {
                await RecordFailureAsync(
                    run,
                    created.ErrorCode ??
                        "model_training_job_create_failed",
                    created.Retryable,
                    cancellationToken);
                return;
            }

            run.ExternalJobId = created.JobId;
            run.State =
                NormalizeProviderState(created.Status);
            run.StartedUtc ??= DateTime.UtcNow;
            run.FailureCode = null;
            run.FailureDetail = null;
            run.LeaseExpiresUtc = null;
            run.UpdatedUtc = DateTime.UtcNow;

            ApplySuccessfulCompletion(
                run,
                created);

            await _db.SaveChangesAsync(
                cancellationToken);
            return;
        }

        var providerJob =
            await _backend.GetTrainingJobAsync(
                run.ExternalJobId,
                cancellationToken);

        if (!providerJob.Succeeded)
        {
            await RecordFailureAsync(
                run,
                providerJob.ErrorCode ??
                    "model_training_job_read_failed",
                providerJob.Retryable,
                cancellationToken);
            return;
        }

        var status =
            providerJob.Status?.Trim().ToLowerInvariant();

        if (status is "failed" or "cancelled")
        {
            run.AttemptCount++;
            run.State = "Failed";
            run.FailureCode =
                "model_training_provider_terminal_failure";
            run.FailureDetail =
                status;
            run.LeaseExpiresUtc = null;
            run.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(
                cancellationToken);
            return;
        }

        run.State =
            NormalizeProviderState(providerJob.Status);
        run.FailureCode = null;
        run.FailureDetail = null;
        run.LeaseExpiresUtc = null;
        run.UpdatedUtc = DateTime.UtcNow;

        ApplySuccessfulCompletion(
            run,
            providerJob);

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    internal static byte[] BuildTrainingJsonl(
        LegendConnectTrainingDatasetManifest manifest)
    {
        using var stream = new MemoryStream();

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(false),
                1024,
                leaveOpen: true);

        foreach (var example in manifest.Training
                     .OrderBy(
                         item => item.EvidenceIdentity,
                         StringComparer.Ordinal))
        {
            var repetitions =
                Math.Clamp(example.Weight, 1, 4);

            for (var i = 0; i < repetitions; i++)
            {
                var line =
                    JsonSerializer.Serialize(new
                    {
                        messages = new object[]
                        {
                            new
                            {
                                role = "system",
                                content =
                                    $"Translate from {example.SourceLanguageCode} to {example.TargetLanguageCode}. Preserve meaning, controlled semantics, context, tone, and register. Return only the target-language text."
                            },
                            new
                            {
                                role = "user",
                                content = example.SourceText
                            },
                            new
                            {
                                role = "assistant",
                                content = example.TargetText,
                                weight = 1
                            }
                        }
                    });

                writer.WriteLine(line);
            }
        }

        writer.Flush();

        return stream.ToArray();
    }

    private async Task<LegendConnectModelTrainingRun?> GetOrCreateRunAsync(
        LegendConnectTrainingDatasetManifest manifest,
        string baseModel,
        CancellationToken cancellationToken)
    {
        var runKey =
            StableHash(
                string.Join(
                    '|',
                    "legend-model-training-v1",
                    manifest.ScopeKey,
                    manifest.DatasetIdentity,
                    Provider,
                    baseModel));

        var existing =
            await _db.Set<LegendConnectModelTrainingRun>()
                .SingleOrDefaultAsync(
                    item => item.RunKey == runKey,
                    cancellationToken);

        if (existing is not null)
            return existing;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var generation =
                (await _db.Set<LegendConnectModelTrainingRun>()
                    .Where(item =>
                        item.ScopeKey == manifest.ScopeKey)
                    .Select(item => (int?)item.Generation)
                    .MaxAsync(cancellationToken) ?? 0) + 1;

            var created =
                new LegendConnectModelTrainingRun
                {
                    Id = Guid.NewGuid(),
                    RunKey = runKey,
                    ScopeKey = manifest.ScopeKey,
                    Generation = generation,
                    DatasetIdentity =
                        manifest.DatasetIdentity,
                    DatasetEvaluatorVersion =
                        manifest.EvaluatorVersion,
                    TrainingProvider = Provider,
                    BaseModel = baseModel,
                    State = "PendingDataset",
                    EvaluationState = "NotStarted",
                    PromotionState = "NotEvaluated",
                    TrainingExampleCount =
                        manifest.Training.Sum(
                            item => Math.Clamp(
                                item.Weight,
                                1,
                                4)),
                    ValidationExampleCount =
                        manifest.HeldOut.Count,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };

            _db.Set<LegendConnectModelTrainingRun>()
                .Add(created);

            try
            {
                await _db.SaveChangesAsync(
                    cancellationToken);
                return created;
            }
            catch (DbUpdateException)
            {
                _db.Entry(created).State =
                    EntityState.Detached;

                existing =
                    await _db.Set<LegendConnectModelTrainingRun>()
                        .SingleOrDefaultAsync(
                            item => item.RunKey == runKey,
                            cancellationToken);

                if (existing is not null)
                    return existing;
            }
        }

        return null;
    }

    private async Task RecordFailureAsync(
        LegendConnectModelTrainingRun run,
        string failureCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        run.AttemptCount++;

        var maxAttempts =
            int.TryParse(
                _configuration[
                    Prefix + "MaximumAttempts"],
                out var configured)
                ? Math.Clamp(configured, 1, 10)
                : DefaultMaximumAttempts;

        run.State =
            retryable &&
            run.AttemptCount < maxAttempts
                ? "PendingRetry"
                : "Failed";

        run.FailureCode =
            failureCode[..Math.Min(
                failureCode.Length,
                120)];

        run.FailureDetail = null;
        run.LeaseExpiresUtc = null;
        run.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(
            cancellationToken);
    }

    private static void ApplySuccessfulCompletion(
        LegendConnectModelTrainingRun run,
        LegendModelTrainingJobResult providerJob)
    {
        if (!string.Equals(
                providerJob.Status,
                "succeeded",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(
                providerJob.ChallengerModelVersion))
        {
            run.State = "Failed";
            run.FailureCode =
                "model_training_missing_challenger";
            return;
        }

        run.ChallengerModelVersion =
            providerJob.ChallengerModelVersion;

        run.State = "TrainingCompleted";
        run.CompletedUtc = DateTime.UtcNow;

        // Phase 7 stops here deliberately.
        run.EvaluationState = "NotStarted";
        run.PromotionState = "NotEvaluated";
        run.PromotedUtc = null;
    }

    private bool Enabled() =>
        bool.TryParse(
            _configuration[
                Prefix + "Enabled"],
            out var enabled) &&
        enabled;

    private static string NormalizeProviderState(
        string? state) =>
        state?.Trim().ToLowerInvariant() switch
        {
            "validating_files" => "ValidatingFiles",
            "queued" => "Queued",
            "running" => "Running",
            "succeeded" => "TrainingCompleted",
            _ => "Submitted"
        };

    private static string StableHash(
        string value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
