using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WeatherImages.Imaging;
using WeatherImages.Models;
using WeatherImages.Services;

namespace WeatherImages.Tests;

public class BuienradarParsingTests
{
    private static IReadOnlyList<StationWeather> Load()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "buienradar-sample.json"));
        return BuienradarClient.Parse(stream);
    }

    [Fact]
    public void Parses_stations_from_the_real_feed_shape()
    {
        var stations = Load();

        Assert.Equal(3, stations.Count);
        Assert.All(stations, s =>
        {
            Assert.True(s.StationId > 0);
            Assert.False(string.IsNullOrWhiteSpace(s.StationName));
            Assert.NotNull(s.Temperature);
        });
    }

    [Fact]
    public void Missing_measurements_become_null_instead_of_failing()
    {
        // Some stations in the feed do not report e.g. "airpressure".
        Assert.Contains(Load(), s => s.AirPressure is null);
    }

    [Fact]
    public void Short_name_drops_the_Meetstation_prefix()
    {
        var station = new StationWeather { StationId = 1, StationName = "Meetstation Arnhem" };
        Assert.Equal("Arnhem", station.ShortName);
    }

    [Fact]
    public void Empty_feed_is_an_error()
    {
        using var stream = new MemoryStream("""{ "actual": {} }"""u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => BuienradarClient.Parse(stream));
    }
}

public class JobStatusCalculatorTests
{
    private static JobEntity Job(string status, int total) => new() { Status = status, Total = total };

    private static JobImageEntity Image(string status) => new() { Status = status };

    private static IReadOnlyCollection<JobImageEntity> Images(int completed, int failed, int pending) =>
        Enumerable.Repeat(Image(ImageStates.Completed), completed)
            .Concat(Enumerable.Repeat(Image(ImageStates.Failed), failed))
            .Concat(Enumerable.Repeat(Image(ImageStates.Pending), pending))
            .ToList();

    [Fact]
    public void Queued_job_stays_queued()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Queued, 0), Images(0, 0, 0));
        Assert.Equal(JobStates.Queued, p.Status);
        Assert.False(p.IsFinished);
    }

    [Fact]
    public void Running_without_total_is_running_with_zero_percent()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Running, 0), Images(0, 0, 0));
        Assert.Equal(JobStates.Running, p.Status);
        Assert.Equal(0, p.Percent);
    }

    [Fact]
    public void Partially_done_job_reports_progress()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Running, 40), Images(10, 0, 30));
        Assert.Equal(JobStates.Running, p.Status);
        Assert.Equal(25, p.Percent);
    }

    [Fact]
    public void All_completed_is_completed()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Running, 40), Images(40, 0, 0));
        Assert.Equal(JobStates.Completed, p.Status);
        Assert.True(p.IsFinished);
        Assert.Equal(100, p.Percent);
    }

    [Fact]
    public void Some_failed_is_completed_with_errors()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Running, 40), Images(39, 1, 0));
        Assert.Equal(JobStates.CompletedWithErrors, p.Status);
        Assert.True(p.IsFinished);
    }

    [Fact]
    public void All_failed_is_failed()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Running, 3), Images(0, 3, 0));
        Assert.Equal(JobStates.Failed, p.Status);
    }

    [Fact]
    public void Failed_job_row_wins()
    {
        var p = JobStatusCalculator.Compute(Job(JobStates.Failed, 0), Images(0, 0, 0));
        Assert.Equal(JobStates.Failed, p.Status);
        Assert.True(p.IsFinished);
    }
}

public class ApiKeyAuthorizerTests
{
    private static HttpRequest RequestWith(string? key)
    {
        var context = new DefaultHttpContext();
        if (key is not null) context.Request.Headers[ApiKeyAuthorizer.HeaderName] = key;
        return context.Request;
    }

    [Fact]
    public void No_configured_key_means_open_api() =>
        Assert.True(ApiKeyAuthorizer.IsAuthorized(RequestWith(null), expectedKey: ""));

    [Fact]
    public void Correct_key_is_accepted() =>
        Assert.True(ApiKeyAuthorizer.IsAuthorized(RequestWith("secret"), "secret"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("secret-but-longer")]
    public void Missing_or_wrong_key_is_rejected(string? provided) =>
        Assert.False(ApiKeyAuthorizer.IsAuthorized(RequestWith(provided), "secret"));
}

public class ImageTests
{
    private static MemoryStream Photo(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(40, 90, 160));
        var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        stream.Position = 0;
        return stream;
    }

    private static readonly StationWeather Station = new()
    {
        StationId = 6275,
        StationName = "Meetstation Arnhem",
        Region = "Arnhem",
        MeasuredAt = "2026-10-27T12:10:00",
        Description = "Zwaar bewolkt",
        Temperature = 15.7,
        FeelTemperature = 15.7,
        Humidity = 80,
        WindSpeed = 6.0,
        WindSpeedBft = 4,
        WindDirection = "Z",
        AirPressure = 1012.6
    };

    [Fact]
    public void Renders_a_jpeg_with_the_same_size_as_the_photo()
    {
        using var photo = Photo(1280, 720);
        using var result = WeatherImageRenderer.Render(photo, Station);

        var info = Image.Identify(result);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal("JPEG", info.Metadata.DecodedImageFormat?.Name);
    }

    [Fact]
    public void Text_panel_is_translucent_so_the_photo_shines_through()
    {
        // Regression: ImageSharp.Drawing ignores brush alpha on Image<Rgb24>, which made the panel solid black.
        using var bright = new Image<Rgb24>(1280, 720, new Rgb24(200, 200, 200));
        using var photo = new MemoryStream();
        bright.SaveAsJpeg(photo);
        photo.Position = 0;

        using var result = WeatherImageRenderer.Render(photo, Station);
        using var rendered = Image.Load<Rgb24>(result);

        var inPanelWithoutText = rendered[1200, 650]; // far right: no text there
        Assert.InRange(inPanelWithoutText.R, 30, 120); // ~35% of 200, not 0 (opaque) and not 200 (no panel)
        Assert.Equal(200, rendered[600, 100].R, tolerance: 3); // above the panel: untouched
    }

    [Fact]
    public void Rendering_changes_the_pixels()
    {
        using var photo = Photo(640, 360);
        var before = photo.ToArray();
        photo.Position = 0;

        using var result = WeatherImageRenderer.Render(photo, Station);
        using var ms = new MemoryStream();
        result.CopyTo(ms);

        Assert.NotEqual(before, ms.ToArray());
    }

    [Fact]
    public void Missing_measurements_render_as_n_a()
    {
        var empty = new StationWeather { StationId = 1, StationName = "Meetstation Test" };
        Assert.Contains("n/a", WeatherImageRenderer.Details(empty));

        using var photo = Photo(640, 360);
        using var result = WeatherImageRenderer.Render(photo, empty); // must not throw
        Assert.True(result.Length > 0);
    }

    [Fact]
    public void Tiff_input_is_rejected_by_the_hardened_decoder()
    {
        using var image = new Image<Rgb24>(16, 16);
        using var tiff = new MemoryStream();
        image.SaveAsTiff(tiff);
        tiff.Position = 0;

        Assert.ThrowsAny<Exception>(() => ImageHelper.GetSafeSize(tiff));
    }

    [Fact]
    public void Oversized_images_are_rejected_before_decoding()
    {
        using var huge = new Image<Rgb24>(5100, 5100, new Rgb24(10, 10, 10)); // 26 megapixels, compresses to almost nothing
        using var stream = new MemoryStream();
        huge.SaveAsJpeg(stream);
        stream.Position = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => ImageHelper.GetSafeSize(stream));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Blob_names_are_stable_and_url_friendly()
    {
        Assert.Equal("abc/6275-arnhem.jpg", ImageStore.BlobNameFor("abc", Station));
        Assert.Equal("de-bilt-zuid", ImageStore.Slug("De Bilt / Zuid!"));
        Assert.Equal("station", ImageStore.Slug("***"));
    }
}
