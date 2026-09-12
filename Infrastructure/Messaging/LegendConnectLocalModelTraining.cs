using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Messaging;

internal sealed record LegendConnectTrainingCheckpoint(string ModelVersion, string AdapterVersion,
    string BaseRepository, string BaseRevision);

/// <summary>One immutable checkpoint verification boundary shared by training
/// completion and runtime registry selection. It grants no promotion rights.</summary>
internal static class LegendConnectTrainingCheckpointAuthority
{
    internal static LegendConnectTrainingCheckpoint? ReadControlledReceipt(IConfiguration configuration, string runKey, string json)
    {
        try
        {
            using var response = JsonDocument.Parse(json);
            var root = response.RootElement;
            if (!LegendConnectModelInferenceTransport.IsControlledAzureResourceId(configuration["LegendConnect:Foundation:AzureResourceId"])) return null;
            if (!IsIdentity(runKey) || root.GetProperty("status").GetString() != "succeeded" ||
                root.GetProperty("run_key").GetString() != runKey ||
                root.GetProperty("model_version").GetString() != "controlled:" + runKey ||
                !root.GetProperty("weights_updated").GetBoolean() || !root.GetProperty("usable_checkpoint").GetBoolean()) return null;
            var bytes = Convert.FromBase64String(root.GetProperty("checkpoint_manifest_base64").GetString()!);
            if (bytes.Length > 16000) return null;
            var identity = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (root.GetProperty("checkpoint_manifest_sha256").GetString() != identity) return null;
            using var manifest = JsonDocument.Parse(bytes);
            var value = manifest.RootElement;
            var repository = value.GetProperty("base_repository").GetString();
            var revision = value.GetProperty("base_model_revision").GetString();
            var configurationJson = root.GetProperty("configuration_json").GetString()!;
            if (Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configurationJson))).ToLowerInvariant() !=
                value.GetProperty("training_configuration_identity").GetString()) return null;
            using var originalConfiguration = JsonDocument.Parse(configurationJson);
            var original = originalConfiguration.RootElement;
            if (original.GetProperty("schema").GetString() != "controlled-transformers-training-v1" ||
                !LegendConnectModelInferenceTransport.IsControlledAzureResourceId(original.GetProperty("azure_resource_id").GetString())) return null;
            var servingHost = root.GetProperty("host_receipt");
            if (servingHost.GetProperty("host_verification").GetString() != "azure-imds-resource-and-tag-v1" ||
                servingHost.GetProperty("azure_resource_id").GetString() != configuration["LegendConnect:Foundation:AzureResourceId"] ||
                !Guid.TryParse(servingHost.GetProperty("azure_vm_id").GetString(), out _)) return null;

            if (value.GetProperty("model_version").GetString() != "controlled:" + runKey ||
                value.GetProperty("adapter_format").GetString() != "peft" ||
                value.GetProperty("azure_resource_id").GetString() != original.GetProperty("azure_resource_id").GetString() ||
                value.GetProperty("trainer_sha256").GetString() != original.GetProperty("trainer_sha256").GetString() ||
                repository != original.GetProperty("base_model").GetString() || revision != original.GetProperty("base_revision").GetString() ||
                repository != configuration["LegendConnect:Foundation:Model"] ||
                revision != configuration["LegendConnect:Foundation:ModelRevision"] ||
                value.GetProperty("dataset_sha256").GetString() != root.GetProperty("dataset_sha256").GetString() ||
                value.GetProperty("training_configuration_identity").GetString() != root.GetProperty("configuration_identity").GetString()) return null;
            foreach (var field in new[] { "adapter_sha256", "adapter_config_sha256", "dataset_sha256", "training_configuration_identity", "base_manifest_sha256", "trainer_sha256" })
                if (!IsIdentity(value.GetProperty(field).GetString())) return null;
            return new("controlled:" + runKey, identity, repository!, revision!);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        { return null; }
    }

    private static bool IsIdentity(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

}
