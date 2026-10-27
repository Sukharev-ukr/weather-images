using System.Text.Json;
using Azure.Storage.Queues;
using WeatherImages.Models;

namespace WeatherImages.Services;

/// <summary>Creates messages on the two Azure Storage queues. Reading and deleting is done by the queue triggers.</summary>
public sealed class JobQueues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly QueueClient _startQueue;
    private readonly QueueClient _imageQueue;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public JobQueues(QueueServiceClient service)
    {
        _startQueue = service.GetQueueClient(Names.StartQueue);
        _imageQueue = service.GetQueueClient(Names.ImageQueue);
    }

    public async Task EnqueueStartAsync(StartJobMessage message, CancellationToken ct)
    {
        await EnsureQueuesAsync(ct);
        await _startQueue.SendMessageAsync(JsonSerializer.Serialize(message, Json), ct);
    }

    public async Task EnqueueImageAsync(ImageJobMessage message, CancellationToken ct)
    {
        await EnsureQueuesAsync(ct);
        await _imageQueue.SendMessageAsync(JsonSerializer.Serialize(message, Json), ct);
    }

    public static T Deserialize<T>(string messageText) =>
        JsonSerializer.Deserialize<T>(messageText, Json)
        ?? throw new InvalidOperationException($"Queue message is not a valid {typeof(T).Name}.");

    // Queues are created by the Bicep template; this also makes local development against Azurite work.
    private async Task EnsureQueuesAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _startQueue.CreateIfNotExistsAsync(cancellationToken: ct);
            await _imageQueue.CreateIfNotExistsAsync(cancellationToken: ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
