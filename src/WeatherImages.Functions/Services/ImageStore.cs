using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using WeatherImages.Models;

namespace WeatherImages.Services;

/// <summary>
/// Stores the generated images in a PRIVATE blob container and hands out short-lived, read-only SAS links.
/// <list type="bullet">
/// <item>Azure (managed identity): user delegation SAS - signed with an Entra ID key, no storage account key involved.</item>
/// <item>Local (Azurite, connection string): account key SAS.</item>
/// </list>
/// </summary>
public sealed class ImageStore
{
    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public ImageStore(BlobServiceClient service)
    {
        _service = service;
        _container = service.GetBlobContainerClient(Names.ImagesContainer);
    }

    public static string BlobNameFor(string jobId, StationWeather station) =>
        $"{jobId}/{station.StationId}-{Slug(station.ShortName)}.jpg";

    public async Task SaveAsync(string blobName, Stream content, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "image/jpeg", CacheControl = "private, max-age=86400" }
        }, ct); // UploadAsync(Stream, BlobUploadOptions) overwrites, which keeps queue retries idempotent.
    }

    /// <summary>Returns a read-only SAS URL for every blob name.</summary>
    public async Task<(IReadOnlyDictionary<string, Uri> Links, DateTimeOffset ExpiresAt)> CreateReadLinksAsync(
        IReadOnlyCollection<string> blobNames, TimeSpan validFor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var startsOn = now.AddMinutes(-5); // tolerate clock skew
        var expiresOn = now.Add(validFor);
        var links = new Dictionary<string, Uri>();

        if (blobNames.Count == 0)
        {
            return (links, expiresOn);
        }

        if (_service.CanGenerateAccountSasUri)
        {
            // Shared key available (Azurite / connection string).
            foreach (var name in blobNames)
            {
                var blob = _container.GetBlobClient(name);
                links[name] = blob.GenerateSasUri(new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
                {
                    BlobContainerName = _container.Name,
                    BlobName = name,
                    Resource = "b",
                    StartsOn = startsOn
                });
            }

            return (links, expiresOn);
        }

        // Managed identity: one delegation key signs all links of this response.
        var key = await _service.GetUserDelegationKeyAsync(startsOn, expiresOn, ct);
        foreach (var name in blobNames)
        {
            var blob = _container.GetBlobClient(name);
            var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
            {
                BlobContainerName = _container.Name,
                BlobName = name,
                Resource = "b",
                StartsOn = startsOn
            }.ToSasQueryParameters(key.Value, _service.AccountName);

            links[name] = new BlobUriBuilder(blob.Uri) { Sas = sas }.ToUri();
        }

        return (links, expiresOn);
    }

    // The container is created by the Bicep template; this also makes local development against Azurite work.
    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    internal static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "station" : slug;
    }
}
