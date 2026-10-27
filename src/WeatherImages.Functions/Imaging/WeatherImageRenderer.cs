using System.Globalization;
using SixLabors.ImageSharp;
using WeatherImages.Models;

namespace WeatherImages.Imaging;

/// <summary>Draws the weather of one station on a photo.</summary>
public static class WeatherImageRenderer
{
    public static Stream Render(Stream photo, StationWeather w)
    {
        // Read the size first so the layout scales with any photo size (layout is designed for 1280x720).
        var size = ImageHelper.GetSafeSize(photo);
        var width = size.Width;
        var height = size.Height;
        var s = height / 720f;
        var x = 48f * s;
        var y0 = height - 340f * s;

        var lines = new List<TextLine>
        {
            new(w.ShortName, x, y0 + 20 * s, (int)(58 * s), "FFFFFF", Bold: true),
            new(Number(w.Temperature, "0.0", " °C"), x, y0 + 84 * s, (int)(120 * s), "FFD54F", Bold: true),
            new(w.Description ?? "-", x, y0 + 226 * s, (int)(34 * s), "FFFFFF"),
            new(Details(w), x, y0 + 272 * s, (int)(26 * s), "E6E6E6"),
            new($"Buienradar  ·  {w.StationName}  ·  measured {FormatTimestamp(w.MeasuredAt)}", x, y0 + 308 * s, (int)(18 * s), "B8B8B8")
        };

        var panel = new Rectangle(0, (int)y0, width, height - (int)y0);
        return ImageHelper.AddTextToImage(photo, panel, "000000A6", lines.ToArray());
    }

    internal static string Details(StationWeather w)
    {
        var parts = new List<string>
        {
            "Feels like " + Number(w.FeelTemperature, "0.0", " °C"),
            "Humidity " + Number(w.Humidity, "0", "%"),
            "Wind " + Number(w.WindSpeed, "0.0", " m/s")
                    + (w.WindSpeedBft is { } bft ? $" ({bft} Bft)" : "")
                    + (string.IsNullOrWhiteSpace(w.WindDirection) ? "" : " " + w.WindDirection),
            Number(w.AirPressure, "0.0", " hPa")
        };

        return string.Join("   |   ", parts);
    }

    internal static string Number(double? value, string format, string suffix) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + suffix : "n/a";

    private static string FormatTimestamp(string? timestamp) =>
        DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : timestamp ?? "unknown";
}
