using Azure;
using Azure.Data.Tables;
using WeatherImages.Models;

namespace WeatherImages.Services;

/// <summary>
/// Stores job progress in Azure Table Storage.
/// <para>
/// Design: every image worker only ever touches its OWN row in <c>jobimages</c>, so parallel workers never
/// fight over the same entity (no ETag conflicts). The overall job status is derived on read
/// (see <see cref="JobStatusCalculator"/>).
/// </para>
/// </summary>
public sealed class JobRepository
{
    private readonly TableClient _jobs;
    private readonly TableClient _images;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public JobRepository(TableServiceClient service)
    {
        _jobs = service.GetTableClient(Names.JobsTable);
        _images = service.GetTableClient(Names.JobImagesTable);
    }

    public async Task CreateAsync(string jobId, int requested, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await _jobs.AddEntityAsync(new JobEntity
        {
            PartitionKey = jobId,
            RowKey = JobEntity.Row,
            Status = JobStates.Queued,
            Requested = requested,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    public async Task<JobEntity?> GetAsync(string jobId, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        var response = await _jobs.GetEntityIfExistsAsync<JobEntity>(jobId, JobEntity.Row, cancellationToken: ct);
        return response.HasValue ? response.Value : null;
    }

    public async Task MarkRunningAsync(string jobId, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await MergeJobAsync(new TableEntity(jobId, JobEntity.Row)
        {
            ["Status"] = JobStates.Running,
            ["StartedAt"] = DateTimeOffset.UtcNow
        }, ct);
    }

    public async Task SetTotalAsync(string jobId, int total, CancellationToken ct) =>
        await MergeJobAsync(new TableEntity(jobId, JobEntity.Row) { ["Total"] = total }, ct);

    public async Task MarkFailedAsync(string jobId, string error, CancellationToken ct) =>
        await MergeJobAsync(new TableEntity(jobId, JobEntity.Row)
        {
            ["Status"] = JobStates.Failed,
            ["Error"] = Truncate(error)
        }, ct);

    /// <summary>Creates a "Pending" row per station. Safe to call again when a queue message is retried.</summary>
    public async Task CreateImageEntriesAsync(string jobId, IEnumerable<StationWeather> stations, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        foreach (var station in stations)
        {
            try
            {
                await _images.AddEntityAsync(new JobImageEntity
                {
                    PartitionKey = jobId,
                    RowKey = JobImageEntity.KeyFor(station.StationId),
                    Status = ImageStates.Pending,
                    StationId = station.StationId,
                    StationName = station.StationName,
                    Region = station.Region
                }, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Already there (retry of the fan-out). Keep whatever state the worker already wrote.
            }
        }
    }

    public async Task MarkImageCompletedAsync(string jobId, int stationId, string blobName, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await _images.UpsertEntityAsync(new TableEntity(jobId, JobImageEntity.KeyFor(stationId))
        {
            ["Status"] = ImageStates.Completed,
            ["BlobName"] = blobName,
            ["CompletedAt"] = DateTimeOffset.UtcNow,
            ["Error"] = ""
        }, TableUpdateMode.Merge, ct);
    }

    public async Task MarkImageFailedAsync(string jobId, int stationId, string error, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await _images.UpsertEntityAsync(new TableEntity(jobId, JobImageEntity.KeyFor(stationId))
        {
            ["Status"] = ImageStates.Failed,
            ["CompletedAt"] = DateTimeOffset.UtcNow,
            ["Error"] = Truncate(error)
        }, TableUpdateMode.Merge, ct);
    }

    public async Task<IReadOnlyList<JobImageEntity>> ListImagesAsync(string jobId, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        var result = new List<JobImageEntity>();
        await foreach (var image in _images.QueryAsync<JobImageEntity>(e => e.PartitionKey == jobId, cancellationToken: ct))
        {
            result.Add(image);
        }

        return result;
    }

    private async Task MergeJobAsync(TableEntity patch, CancellationToken ct) =>
        await _jobs.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);

    // Tables are created by the Bicep template; this also makes local development against Azurite work.
    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _jobs.CreateIfNotExistsAsync(ct);
            await _images.CreateIfNotExistsAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
