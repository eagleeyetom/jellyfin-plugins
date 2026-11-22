#!/usr/bin/env dotnet-script
#r "nuget: SixLabors.ImageSharp, 3.1.4"
#r "nuget: SixLabors.ImageSharp.Drawing, 2.1.3"
#r "nuget: SixLabors.Fonts, 2.0.2"

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;

var outputPath = "Jellyfin.Plugin.BadgeFin/Assets";
Directory.CreateDirectory(outputPath);

// Badge configurations: (filename, text, background color, text color)
var badges = new (string file, string text, Color bg, Color fg)[]
{
    ("badge-4k.png", "4K", Color.FromRgb(255, 193, 7), Color.Black),      // Gold/Amber
    ("badge-1080p.png", "1080p", Color.FromRgb(224, 224, 224), Color.Black), // Light gray
    ("badge-720p.png", "720p", Color.FromRgb(158, 158, 158), Color.Black),   // Medium gray
    ("badge-sd.png", "SD", Color.FromRgb(97, 97, 97), Color.White)           // Dark gray
};

// Try to load a system font
FontFamily fontFamily;
if (!SystemFonts.TryGet("Arial", out fontFamily) &&
    !SystemFonts.TryGet("DejaVu Sans", out fontFamily) &&
    !SystemFonts.TryGet("Liberation Sans", out fontFamily))
{
    Console.WriteLine("Warning: No suitable font found, using fallback");
    fontFamily = SystemFonts.Families.FirstOrDefault();
}

var font = fontFamily.CreateFont(24, FontStyle.Bold);

foreach (var (filename, text, bgColor, textColor) in badges)
{
    int width = text.Length > 2 ? 90 : 50;
    int height = 32;
    float cornerRadius = 6f;

    using var image = new Image<Rgba32>(width, height, Color.Transparent);

    // Create rounded rectangle path
    var roundedRect = new RoundedRectangle(0, 0, width, height, cornerRadius);

    image.Mutate(x =>
    {
        // Fill background
        x.Fill(bgColor, roundedRect);

        // Draw text centered
        var textOptions = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(width / 2f, height / 2f)
        };
        x.DrawText(textOptions, text, textColor);
    });

    var path = Path.Combine(outputPath, filename);
    image.SaveAsPng(path);
    Console.WriteLine($"Generated: {path}");
}

Console.WriteLine("Badge generation complete!");

// Helper class for rounded rectangle
public class RoundedRectangle : IPath
{
    private readonly IPath _path;

    public RoundedRectangle(float x, float y, float width, float height, float cornerRadius)
    {
        var builder = new PathBuilder();

        // Top-left corner
        builder.MoveTo(new PointF(x + cornerRadius, y));

        // Top edge and top-right corner
        builder.LineTo(new PointF(x + width - cornerRadius, y));
        builder.ArcTo(cornerRadius, cornerRadius, 0, false, true, new PointF(x + width, y + cornerRadius));

        // Right edge and bottom-right corner
        builder.LineTo(new PointF(x + width, y + height - cornerRadius));
        builder.ArcTo(cornerRadius, cornerRadius, 0, false, true, new PointF(x + width - cornerRadius, y + height));

        // Bottom edge and bottom-left corner
        builder.LineTo(new PointF(x + cornerRadius, y + height));
        builder.ArcTo(cornerRadius, cornerRadius, 0, false, true, new PointF(x, y + height - cornerRadius));

        // Left edge and top-left corner
        builder.LineTo(new PointF(x, y + cornerRadius));
        builder.ArcTo(cornerRadius, cornerRadius, 0, false, true, new PointF(x + cornerRadius, y));

        builder.CloseFigure();
        _path = builder.Build();
    }

    public RectangleF Bounds => _path.Bounds;
    public PathTypes PathType => _path.PathType;
    public IPath AsClosedPath() => _path.AsClosedPath();
    public IPath Flatten() => _path.Flatten();
    public SegmentInfo PointAlongPath(float distance) => _path.PointAlongPath(distance);
    public IEnumerable<ISimplePath> Flatten(float flatness) => _path.Flatten(flatness);
    public bool Contains(PointF point) => _path.Contains(point);
    public IPath Transform(System.Numerics.Matrix3x2 matrix) => _path.Transform(matrix);
}
