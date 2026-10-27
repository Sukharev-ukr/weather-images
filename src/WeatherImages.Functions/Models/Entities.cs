using Azure;
using Azure.Data.Tables;

namespace WeatherImages.Models;

/// <summary>Row in table <c>jobs</c>: PartitionKey = jobId, RowKey = "job".</summary>
public sealed class JobEntity : ITableEntity
{
    public const string Row = "job";

    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = Row;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Status { get; set; } = JobStates.Queued;
    public int Requested { get; set; }

    /// <summary>Number of images that will be produced (0 until the stations are known).</summary>
    public int Total { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>Row in table <c>jobimages</c>: PartitionKey = jobId, RowKey = zero padded stationId.</summary>
public sealed class JobImageEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Status { get; set; } = ImageStates.Pending;
    public int StationId { get; set; }
    public string StationName { get; set; } = "";
    public string? Region { get; set; }
    public string? BlobName { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public static string KeyFor(int stationId) => stationId.ToString("D6");
}
