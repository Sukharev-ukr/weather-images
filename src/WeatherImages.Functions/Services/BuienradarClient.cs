using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WeatherImages.Models;

namespace WeatherImages.Services;

/// <summary>Reads the current measurements of all stations from https://data.buienradar.nl/2.0/feed/json.</summary>
public sealed class BuienradarClient(HttpClient http, IOptions<WeatherImagesOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public async Task<IReadOnlyList<StationWeather>> GetStationsAsync(CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(options.Value.BuienradarUrl, ct);
        return Parse(stream);
    }

    public static IReadOnlyList<StationWeather> Parse(Stream json)
    {
        var feed = JsonSerializer.Deserialize<Feed>(json, Json)
                   ?? throw new InvalidOperationException("Buienradar returned an empty response.");

        var measurements = feed.Actual?.StationMeasurements
                           ?? throw new InvalidOperationException("Buienradar feed has no 'actual.stationmeasurements'.");

        return measurements
            .Where(m => m.StationId > 0 && !string.IsNullOrWhiteSpace(m.StationName))
            .GroupBy(m => m.StationId)
            .Select(g => g.First())
            .Select(m => new StationWeather
            {
                StationId = m.StationId,
                StationName = m.StationName!,
                Region = m.Regio,
                MeasuredAt = m.Timestamp,
                Description = m.WeatherDescription,
                Temperature = m.Temperature,
                FeelTemperature = m.FeelTemperature,
                Humidity = m.Humidity,
                WindSpeed = m.WindSpeed,
                WindSpeedBft = m.WindSpeedBft,
                WindDirection = m.WindDirection,
                AirPressure = m.AirPressure,
                RainFallLastHour = m.RainFallLastHour
            })
            .ToList();
    }

    // --- DTOs for the parts of the feed we use (property names match case-insensitively) ---
    private sealed class Feed { public Actual? Actual { get; set; } }

    private sealed class Actual { public List<Measurement>? StationMeasurements { get; set; } }

    private sealed class Measurement
    {
        public int StationId { get; set; }
        public string? StationName { get; set; }
        public string? Regio { get; set; }
        public string? Timestamp { get; set; }
        public string? WeatherDescription { get; set; }
        public double? Temperature { get; set; }
        public double? FeelTemperature { get; set; }
        public double? Humidity { get; set; }
        public double? WindSpeed { get; set; }
        public int? WindSpeedBft { get; set; }
        public string? WindDirection { get; set; }
        public double? AirPressure { get; set; }
        public double? RainFallLastHour { get; set; }
    }
}
