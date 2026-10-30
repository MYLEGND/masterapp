using Azure.Identity;
using Azure.Storage.Blobs;
namespace AgentPortal.Services;

// Existing agent document storage configuration: shared by ordinary documents and immutable case evidence.
public static class AgentDocumentStorageLocation
{
    public static string GetBaseRoot(IWebHostEnvironment env)
    {
        var configured = Environment.GetEnvironmentVariable("LEGEND_AGENT_DOCUMENTS_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        var fallback = Path.Combine(env.ContentRootPath, "App_Data", "agent-documents");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static BlobContainerClient? BuildBlobContainerClient(IConfiguration configuration)
    {
        var connString = configuration["AgentDocuments:StorageConnectionString"];
        var containerName = configuration["AgentDocuments:ContainerName"];
        if (!string.IsNullOrWhiteSpace(connString) && !string.IsNullOrWhiteSpace(containerName))
        {
            return new BlobContainerClient(connString, containerName);
        }

        var containerUrl = configuration["AgentDocuments:BlobContainerUrl"];
        if (!string.IsNullOrWhiteSpace(containerUrl))
        {
            return new BlobContainerClient(new Uri(containerUrl), new DefaultAzureCredential());
        }

        return null;
    }

}
