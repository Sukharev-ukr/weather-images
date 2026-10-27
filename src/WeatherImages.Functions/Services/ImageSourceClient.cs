using Microsoft.Extensions.Options;

namespace WeatherImages.Services;

/// <summary>
/// Downloads a public background photo. Default: Lorem Picsum (https://picsum.photos), which needs no API key.
/// The seed makes the photo different per job and station, but stable when a queue message is retried.
/// </summary>
public sealed class ImageSourceClient(HttpClient http, IOptions<WeatherImagesOptions> options)
{
    public async Task<Stream> DownloadAsync(string seed, CancellationToken ct)
    {
        var o = options.Value;
        var url = o.ImageUrlTemplate
            .Replace("{seed}", Uri.EscapeDataString(seed))
            .Replace("{width}", o.ImageWidth.ToString())
            .Replace("{height}", o.ImageHeight.ToString());

        var bytes = await http.GetByteArrayAsync(url, ct); // follows redirects
        return new MemoryStream(bytes);
    }
}
