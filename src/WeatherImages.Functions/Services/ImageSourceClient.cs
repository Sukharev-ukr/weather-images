using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WeatherImages.Imaging;

namespace WeatherImages.Services;

/// <summary>
/// Gets a background photo for one weather image. Public image services go down (Lorem Picsum was unreachable for
/// days in November 2026 and every image of a job failed), so this never depends on a single source:
/// <list type="number">
/// <item>The primary public image API (<see cref="WeatherImagesOptions.ImageUrlTemplate"/>, default Lorem Picsum).</item>
/// <item>A fixed photo from <see cref="WeatherImagesOptions.FallbackImageUrls"/> (Unsplash CDN), picked by the seed.</item>
/// <item>A background generated locally from the seed, so the weather image is ALWAYS produced.</item>
/// </list>
/// Each download gets its own short timeout, so a dead service costs seconds, not the whole job.
/// The seed makes the photo different per job and station, but stable when a queue message is retried.
/// </summary>
public sealed class ImageSourceClient(
    HttpClient http,
    IOptions<WeatherImagesOptions> options,
    ILogger<ImageSourceClient> logger)
{
    public async Task<Stream> DownloadAsync(string seed, CancellationToken ct)
    {
        var o = options.Value;

        foreach (var url in CandidateUrls(o, seed))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.ImageDownloadTimeoutSeconds)));

                var bytes = await http.GetByteArrayAsync(url, attempt.Token); // follows redirects
                var stream = new MemoryStream(bytes);
                ImageHelper.GetSafeSize(stream); // throws when the response is not a readable image
                return stream;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Background photo from {Host} failed ({Error}); trying the next source",
                    SafeHost(url), ex.GetBaseException().Message);
            }
        }

        logger.LogWarning("No background photo source answered for {Seed}; using a generated background", seed);
        return CreateGeneratedBackground(seed, o.ImageWidth, o.ImageHeight);
    }

    /// <summary>Primary URL first, then one fallback photo chosen by the seed (then the rest, in order).</summary>
    internal static IEnumerable<string> CandidateUrls(WeatherImagesOptions o, string seed)
    {
        if (!string.IsNullOrWhiteSpace(o.ImageUrlTemplate))
        {
            yield return Fill(o.ImageUrlTemplate, seed, o);
        }

        var fallbacks = o.FallbackImageUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
        if (fallbacks.Length == 0)
        {
            yield break;
        }

        // Two different fallback photos at most: enough to ride out one broken link without making a dead CDN slow.
        var first = (int)(StableHash(seed) % (uint)fallbacks.Length);
        yield return Fill(fallbacks[first], seed, o);
        if (fallbacks.Length > 1)
        {
            yield return Fill(fallbacks[(first + 1) % fallbacks.Length], seed, o);
        }
    }

    /// <summary>A diagonal two-colour gradient, deterministic per seed. Used only when no photo source works.</summary>
    internal static Stream CreateGeneratedBackground(string seed, int width, int height)
    {
        var hash = StableHash(seed);
        var hue = hash % 360;
        var from = FromHsv(hue, 0.55f, 0.75f);
        var to = FromHsv((hue + 40) % 360, 0.70f, 0.35f);

        using var image = new Image<Rgba32>(width, height);
        image.Mutate(x => x.Fill(new LinearGradientBrush(
            new PointF(0, 0), new PointF(width, height), GradientRepetitionMode.None,
            new ColorStop(0f, from), new ColorStop(1f, to))));

        var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = 90 });
        output.Position = 0;
        return output;
    }

    private static string Fill(string template, string seed, WeatherImagesOptions o) => template
        .Replace("{seed}", Uri.EscapeDataString(seed))
        .Replace("{width}", o.ImageWidth.ToString())
        .Replace("{height}", o.ImageHeight.ToString());

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid url)";

    /// <summary>FNV-1a: unlike string.GetHashCode it is the same in every process, so retries pick the same photo.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }
        return hash;
    }

    private static Color FromHsv(uint hue, float saturation, float value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60f % 2 - 1));
        var m = value - c;
        var (r, g, b) = (hue / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
