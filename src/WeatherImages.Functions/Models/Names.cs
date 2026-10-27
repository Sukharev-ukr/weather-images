namespace WeatherImages.Models;

/// <summary>Names of the Azure Storage resources. Keep in sync with infra/main.bicep.</summary>
public static class Names
{
    /// <summary>Queue 1: "start a job". One message per POST /api/jobs.</summary>
    public const string StartQueue = "start-jobs";

    /// <summary>Queue 2: "render one image". One message per weather station (fan-out).</summary>
    public const string ImageQueue = "image-jobs";

    public const string ImagesContainer = "images";

    /// <summary>Table with one row per job (status header).</summary>
    public const string JobsTable = "jobs";

    /// <summary>Table with one row per image of a job (progress per station).</summary>
    public const string JobImagesTable = "jobimages";

    /// <summary>Must match "maxDequeueCount" in host.json (the runtime moves the message to *-poison afterwards).</summary>
    public const int MaxDequeueCount = 5;
}

public static class JobStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string CompletedWithErrors = "CompletedWithErrors";
    public const string Failed = "Failed";
}

public static class ImageStates
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
