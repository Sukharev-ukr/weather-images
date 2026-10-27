using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace WeatherImages.Imaging;

/// <summary>A piece of text to draw on an image.</summary>
public readonly record struct TextLine(string Text, float X, float Y, int FontSize, string ColorHex, bool Bold = false);

/// <summary>
/// Based on the course sample (wearetriple/InHolland-CCD, 2026/samples/ImageEditor).
/// Differences: the font is embedded in the assembly (Linux hosts in Azure have no guaranteed system fonts),
/// text can be bold, an optional translucent panel can be drawn behind the text, and the result is a JPEG.
/// </summary>
public static class ImageHelper
{
    private const string FamilyName = "Liberation Sans";
    private const long MaxPixels = 25_000_000; // refuse "decompression bomb" photos before decoding them
    private static readonly Lazy<FontFamily> Family = new(LoadEmbeddedFonts);

    /// <summary>
    /// The photo comes from the internet, so decode defensively: only JPEG and PNG decoders are registered
    /// (no TIFF/BigTIFF/GIF/WebP...), only the first frame is read and metadata (EXIF/ICC) is skipped.
    /// </summary>
    public static readonly DecoderOptions SafeDecoderOptions = new()
    {
        Configuration = new Configuration(new JpegConfigurationModule(), new PngConfigurationModule()),
        MaxFrames = 1,
        SkipMetadata = true
    };

    /// <summary>Reads width/height without decoding the pixels and rejects oversized images.</summary>
    public static Size GetSafeSize(Stream imageStream)
    {
        var info = Image.Identify(SafeDecoderOptions, imageStream);
        imageStream.Position = 0;

        if ((long)info.Width * info.Height > MaxPixels)
        {
            throw new InvalidOperationException($"Image of {info.Width}x{info.Height} pixels is too large.");
        }

        return new Size(info.Width, info.Height);
    }

    /// <param name="imageStream">Source image (any format ImageSharp can read).</param>
    /// <param name="panel">Optional rectangle (x, y, width, height) filled with <paramref name="panelColorHex"/> behind the text.</param>
    public static Stream AddTextToImage(
        Stream imageStream,
        Rectangle? panel,
        string panelColorHex,
        params TextLine[] texts)
    {
        GetSafeSize(imageStream);
        // Rgba32 on purpose: ImageSharp.Drawing ignores the alpha of a brush when the target is Rgb24 (what a JPEG decodes to),
        // which would make the panel fully opaque. The alpha channel is simply dropped again when saving as JPEG.
        using var image = Image.Load<Rgba32>(SafeDecoderOptions, imageStream);

        image.Mutate(img =>
        {
            if (panel is { } p)
            {
                // SrcOver blend: the colour's alpha channel ("RRGGBBAA", e.g. 000000A6) lets the photo shine through.
                var blend = new DrawingOptions
                {
                    GraphicsOptions = new GraphicsOptions { AlphaCompositionMode = PixelAlphaCompositionMode.SrcOver }
                };
                img.Fill(blend, new SolidBrush(Color.ParseHex(panelColorHex)), new RectangularPolygon(p.X, p.Y, p.Width, p.Height));
            }

            foreach (var line in texts)
            {
                var font = Family.Value.CreateFont(line.FontSize, line.Bold ? FontStyle.Bold : FontStyle.Regular);
                var options = new RichTextOptions(font)
                {
                    Origin = new PointF(line.X, line.Y),
                    WrappingLength = Math.Max(10, image.Width - line.X - 16)
                };

                img.DrawText(options, line.Text, Color.ParseHex(line.ColorHex));
            }
        });

        var output = new MemoryStream();
        // 4:4:4 (no chroma subsampling) keeps coloured text crisp on top of the photo.
        image.SaveAsJpeg(output, new JpegEncoder { Quality = 88, ColorType = JpegEncodingColor.YCbCrRatio444 });
        output.Position = 0;
        return output;
    }

    private static FontFamily LoadEmbeddedFonts()
    {
        var assembly = typeof(ImageHelper).Assembly;
        var collection = new FontCollection();
        FontFamily? family = null;

        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                               ?? throw new InvalidOperationException($"Embedded font '{resource}' not found.");
            family = collection.Add(stream);
        }

        return family ?? throw new InvalidOperationException($"No embedded fonts found for '{FamilyName}'.");
    }
}
