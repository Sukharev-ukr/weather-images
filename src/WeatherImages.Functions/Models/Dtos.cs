namespace WeatherImages.Models;

/// <summary>Optional JSON body of POST /api/jobs.</summary>
public sealed class StartJobRequest
{
    /// <summary>How many weather stations to render (1-50, default 50).</summary>
    public int? Count { get; set; }
}

public sealed record StartJobResponse(string JobId, string Status, string StatusUrl, string ResultsUrl);

public sealed record JobStatusResponse(
    string JobId,
    string Status,
    int Requested,
    int Total,
    int Completed,
    int Failed,
    int PercentComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    string? Error,
    string ResultsUrl);

public sealed record ImageLink(int StationId, string StationName, string? Region, string FileName, string Url);

public sealed record JobImagesResponse(
    string JobId,
    string Status,
    bool IsComplete,
    int Total,
    int Completed,
    int Failed,
    DateTimeOffset? LinksExpireAt,
    IReadOnlyList<ImageLink> Images);
