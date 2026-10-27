namespace WeatherImages.Models;

/// <summary>Current measurements of one Buienradar weather station (all measurements are optional in the feed).</summary>
public sealed record StationWeather
{
    public int StationId { get; init; }
    public string StationName { get; init; } = "";
    public string? Region { get; init; }
    public string? MeasuredAt { get; init; }
    public string? Description { get; init; }
    public double? Temperature { get; init; }
    public double? FeelTemperature { get; init; }
    public double? Humidity { get; init; }
    public double? WindSpeed { get; init; }
    public int? WindSpeedBft { get; init; }
    public string? WindDirection { get; init; }
    public double? AirPressure { get; init; }
    public double? RainFallLastHour { get; init; }

    /// <summary>"Meetstation Arnhem" -> "Arnhem".</summary>
    public string ShortName =>
        StationName.StartsWith("Meetstation ", StringComparison.OrdinalIgnoreCase)
            ? StationName["Meetstation ".Length..]
            : StationName;
}
