using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeatherImages.Imaging;
using WeatherImages.Models;
using WeatherImages.Services;

namespace WeatherImages.Functions;

/// <summary>
/// The two background workers. The queue trigger reads a message, runs the function and DELETES the message
/// when the function returns successfully. When it throws, the message becomes visible again (retry) and is moved
/// to "&lt;queue&gt;-poison" after <see cref="Names.MaxDequeueCount"/> attempts.
/// </summary>
public sealed class QueueWorkers(
    BuienradarClient buienradar,
    ImageSourceClient imageSource,
    JobRepository jobs,
    JobQueues queues,
    ImageStore imageStore,
    IOptions<WeatherImagesOptions> options,
    ILogger<QueueWorkers> logger)
{
    /// <summary>
    /// Queue 1 (start-jobs): fetch the weather stations once, then FAN OUT:
    /// one message on queue 2 (image-jobs) per station.
    /// </summary>
    [Function("FanOutJob")]
    public async Task FanOutJob([QueueTrigger(Names.StartQueue)] QueueMessage message, CancellationToken ct)
    {
        var start = JobQueues.Deserialize<StartJobMessage>(message.MessageText);

        try
        {
            await jobs.MarkRunningAsync(start.JobId, ct);

            var stations = (await buienradar.GetStationsAsync(ct))
                .Take(Math.Min(start.Count, options.Value.MaxStations))
                .ToList();

            if (stations.Count == 0)
            {
                throw new InvalidOperationException("Buienradar returned no weather stations.");
            }

            // Order matters: rows first, then total, then the messages - so a status read never sees a
            // finished worker for a station that has no row, or a total that is too small.
            await jobs.CreateImageEntriesAsync(start.JobId, stations, ct);
            await jobs.SetTotalAsync(start.JobId, stations.Count, ct);

            for (var i = 0; i < stations.Count; i++)
            {
                await queues.EnqueueImageAsync(new ImageJobMessage(start.JobId, i, stations[i]), ct);
            }

            logger.LogInformation("Job {JobId}: fanned out {Count} image jobs", start.JobId, stations.Count);
        }
        catch (Exception ex) when (message.DequeueCount >= Names.MaxDequeueCount)
        {
            // Last attempt: record the failure in the table instead of leaving the job "Running" forever.
            logger.LogError(ex, "Job {JobId}: giving up after {Attempts} attempts", start.JobId, message.DequeueCount);
            await jobs.MarkFailedAsync(start.JobId, ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// Queue 2 (image-jobs): download a public photo, write the station's weather on it,
    /// store the result in Blob Storage and update the progress in Table Storage.
    /// </summary>
    [Function("GenerateImage")]
    public async Task GenerateImage([QueueTrigger(Names.ImageQueue)] QueueMessage message, CancellationToken ct)
    {
        var job = JobQueues.Deserialize<ImageJobMessage>(message.MessageText);
        var station = job.Station;

        try
        {
            await using var photo = await imageSource.DownloadAsync($"{job.JobId}-{station.StationId}", ct);
            await using var rendered = WeatherImageRenderer.Render(photo, station);

            var blobName = ImageStore.BlobNameFor(job.JobId, station);
            await imageStore.SaveAsync(blobName, rendered, ct);
            await jobs.MarkImageCompletedAsync(job.JobId, station.StationId, blobName, ct);

            logger.LogInformation("Job {JobId}: image for {Station} stored as {Blob}", job.JobId, station.StationName, blobName);
        }
        catch (Exception ex) when (message.DequeueCount >= Names.MaxDequeueCount)
        {
            logger.LogError(ex, "Job {JobId}: image for {Station} failed for good", job.JobId, station.StationName);
            await jobs.MarkImageFailedAsync(job.JobId, station.StationId, ex.Message, CancellationToken.None);
        }
    }
}
