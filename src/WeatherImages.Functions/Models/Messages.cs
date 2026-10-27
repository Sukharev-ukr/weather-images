namespace WeatherImages.Models;

/// <summary>Queue message on <see cref="Names.StartQueue"/>: "fetch the stations and fan out".</summary>
public sealed record StartJobMessage(string JobId, int Count);

/// <summary>Queue message on <see cref="Names.ImageQueue"/>: "render the image of this one station".</summary>
public sealed record ImageJobMessage(string JobId, int Index, StationWeather Station);
