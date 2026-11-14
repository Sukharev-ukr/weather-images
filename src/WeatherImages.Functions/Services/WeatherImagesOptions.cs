namespace WeatherImages.Services;

/// <summary>
/// Bound from app settings with the prefix <c>WeatherImages__</c>, e.g. <c>WeatherImages__ApiKey</c>.
/// </summary>
public sealed class WeatherImagesOptions
{
    public const string SectionName = "WeatherImages";

    /// <summary>
    /// Key required (header <c>x-api-key</c>) to start a job. Leave empty to disable authentication.
    /// The GET endpoints stay anonymous: the job id (a random GUID) acts as the capability to read a job.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Upper limit of weather stations per job (the assignment asks for 50).</summary>
    public int MaxStations { get; set; } = 50;

    /// <summary>How long the SAS links in the results stay valid.</summary>
    public int SasExpiryMinutes { get; set; } = 1440;

    public string BuienradarUrl { get; set; } = "https://data.buienradar.nl/2.0/feed/json";

    /// <summary>Public image API. No API key needed. Placeholders: {seed} {width} {height}.</summary>
    public string ImageUrlTemplate { get; set; } = "https://picsum.photos/seed/{seed}/{width}/{height}";

    /// <summary>
    /// Used when the primary image API fails (it was down for days in November 2026). Fixed photos on the Unsplash
    /// CDN (no API key); one is picked per seed. Placeholders: {width} {height}. When these fail too, a background
    /// is generated locally, so a job never fails only because a photo service is down.
    /// </summary>
    public string[] FallbackImageUrls { get; set; } =
    [
        "https://images.unsplash.com/photo-1501785888041-af3ef285b470?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1506905925346-21bda4d32df4?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1470071459604-3b5ec3a7fe05?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1441974231531-c6227db76b6e?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1500530855697-b586d89ba3ee?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1469474968028-56623f02e42e?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1472214103451-9374bd1c798e?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1433086966358-54859d0ed716?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1447752875215-b2761acb3c5d?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1464822759023-fed622ff2c3b?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1504384308090-c894fdcc538d?w={width}&h={height}&fit=crop&fm=jpg&q=80",
        "https://images.unsplash.com/photo-1513836279014-a89f7a76ae86?w={width}&h={height}&fit=crop&fm=jpg&q=80",
    ];

    /// <summary>Timeout per photo download attempt, so a dead image service costs seconds, not minutes.</summary>
    public int ImageDownloadTimeoutSeconds { get; set; } = 10;

    public int ImageWidth { get; set; } = 1280;
    public int ImageHeight { get; set; } = 720;
}
