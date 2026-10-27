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

    public int ImageWidth { get; set; } = 1280;
    public int ImageHeight { get; set; } = 720;
}
