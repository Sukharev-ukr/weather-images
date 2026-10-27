using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;

namespace WeatherImages.Services;

/// <summary>
/// Creates the three storage clients from the same settings the Functions host uses for <c>AzureWebJobsStorage</c>:
/// <list type="bullet">
/// <item>Local (Azurite): <c>AzureWebJobsStorage = UseDevelopmentStorage=true</c> (connection string).</item>
/// <item>Azure: <c>AzureWebJobsStorage__accountName</c> (+ optional <c>__clientId</c>) and a managed identity. No keys anywhere.</item>
/// </list>
/// </summary>
public static class StorageClientFactory
{
    private const string Setting = "AzureWebJobsStorage";

    public static BlobServiceClient CreateBlobServiceClient(IConfiguration config)
    {
        if (TryGetConnectionString(config, out var cs))
        {
            return new BlobServiceClient(cs);
        }

        return new BlobServiceClient(ServiceUri(config, "blob"), CreateCredential(config));
    }

    public static QueueServiceClient CreateQueueServiceClient(IConfiguration config)
    {
        // The Functions queue trigger expects base64 messages by default (host.json: messageEncoding).
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };

        if (TryGetConnectionString(config, out var cs))
        {
            return new QueueServiceClient(cs, options);
        }

        return new QueueServiceClient(ServiceUri(config, "queue"), CreateCredential(config), options);
    }

    public static TableServiceClient CreateTableServiceClient(IConfiguration config)
    {
        if (TryGetConnectionString(config, out var cs))
        {
            return new TableServiceClient(cs);
        }

        return new TableServiceClient(ServiceUri(config, "table"), CreateCredential(config));
    }

    private static bool TryGetConnectionString(IConfiguration config, out string connectionString)
    {
        connectionString = config[Setting] ?? "";
        return !string.IsNullOrWhiteSpace(connectionString);
    }

    private static Uri ServiceUri(IConfiguration config, string service)
    {
        var account = config[$"{Setting}__accountName"];
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new InvalidOperationException(
                $"Configure '{Setting}' (connection string, local) or '{Setting}__accountName' (managed identity, Azure).");
        }

        return new Uri($"https://{account}.{service}.core.windows.net");
    }

    private static TokenCredential CreateCredential(IConfiguration config)
    {
        var clientId = config[$"{Setting}__clientId"];
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            // Needed for a user-assigned managed identity; empty = system-assigned / developer login.
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId
        });
    }
}
