using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>How a threshold is chosen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThresholdMode>))]
public enum ThresholdMode
{
    /// <summary>Automatic (Otsu) — splits the histogram into two groups. Good when the image clearly has object and background.</summary>
    Otsu,

    /// <summary>A fixed value (0–255).</summary>
    Fixed,
}

/// <summary>Blob parameters as one object — keep it in the setup class and pass it to <see cref="Blob.Find(VisionImage, BlobParams)"/>. Saved as plain JSON;
/// do not rename properties after release (old recipes would silently fall back to defaults).</summary>
public sealed class BlobParams
{
    /// <summary>Threshold mode. Default <see cref="ThresholdMode.Otsu"/>.</summary>
    public ThresholdMode Mode { get; set; } = ThresholdMode.Otsu;

    /// <summary>Threshold (0–255) when <see cref="Mode"/> is <see cref="ThresholdMode.Fixed"/>.</summary>
    public int Threshold { get; set; } = 128;

    /// <summary>Find dark objects (pixels below the threshold) instead of bright ones.</summary>
    public bool Invert { get; set; }

    /// <summary>Smallest blob area in px² to keep.</summary>
    public int MinArea { get; set; } = 50;

    /// <summary>Largest blob area in px² to keep.</summary>
    public int MaxArea { get; set; } = int.MaxValue;
}

/// <summary>
/// A connected region found by thresholding: area, center, bounding box, outline, and the rotated box around it (angle, length, width).
/// Coordinates are in the image it was found in; drawing it with <c>ctx.Draw</c> converts them to the original image.
/// </summary>
/// <example>
/// Count the holes in a ROI (dark blobs):
/// <code>
/// job.Add("holes", roi: () =&gt; roi, ctx =&gt;
/// {
///     var holes = Blob.Find(ctx.Image, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
///     ctx.Draw(holes);
///     ctx.Value("holes", holes.Length, min: 4, max: 4, code: "HOLE");
/// });
/// </code>
/// </example>
public sealed class Blob
{
    private Blob(double area, Point2d center, Rect bounds, Point[] contour, double angle, double length, double width, Affine toSource)
    {
        Area = area;
        Center = center;
        Bounds = bounds;
        Contour = contour;
        Angle = angle;
        Length = length;
        Width = width;
        ToSource = toSource;
    }

    /// <summary>Area in px² (inside the outline).</summary>
    public double Area { get; }

    /// <summary>Center of mass (px).</summary>
    public Point2d Center { get; }

    /// <summary>Axis-aligned bounding box (px).</summary>
    public Rect Bounds { get; }

    /// <summary>Outer outline points (px).</summary>
    public Point[] Contour { get; }

    /// <summary>Direction of the long axis in degrees, clockwise on screen is +, range (−90, 90] (a long axis has no front or back).</summary>
    public double Angle { get; }

    /// <summary>Length along the long axis of the smallest rotated box around the blob (px).</summary>
    public double Length { get; }

    /// <summary>Width across the long axis of the smallest rotated box around the blob (px).</summary>
    public double Width { get; }

    internal Affine ToSource { get; }

    /// <summary>Finds blobs, largest first.</summary>
    /// <param name="image">The image (color is converted to gray).</param>
    /// <param name="mode">Threshold mode.</param>
    /// <param name="threshold">Threshold (0–255) for <see cref="ThresholdMode.Fixed"/>.</param>
    /// <param name="invert">Find dark objects instead of bright ones.</param>
    /// <param name="minArea">Smallest area to keep (px²).</param>
    /// <param name="maxArea">Largest area to keep (px²).</param>
    /// <returns>The blobs, largest first (empty when none).</returns>
    /// <remarks>
    /// Only outer outlines are found (a hole inside a blob is not a blob of its own). Automatic thresholding (<see cref="ThresholdMode.Otsu"/>) always splits the image in two,
    /// so when the object may be missing it reports parts of the background: use <see cref="ThresholdMode.Fixed"/> for presence checks.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    public static Blob[] Find(VisionImage image, ThresholdMode mode = ThresholdMode.Otsu, int threshold = 128, bool invert = false, int minArea = 50, int maxArea = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var bin = image.Threshold(mode, threshold, invert);
        Cv2.FindContours(bin.Mat, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
        return FromContours(contours, minArea, maxArea, image.ToSourceTransform);
    }

    internal static Blob[] FromContours(Point[][] contours, double minArea, double maxArea, Affine toSource)
    {
        var list = new List<Blob>();
        foreach (var c in contours)
        {
            var area = Cv2.ContourArea(c);
            if (area < minArea || area > maxArea || c.Length < 3)
                continue;
            var m = Cv2.Moments(c);
            var bounds = Cv2.BoundingRect(c);
            var center = m.M00 > 0 ? new Point2d(m.M10 / m.M00, m.M01 / m.M00) : new Point2d(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
            // 주축 방향 (2차 모멘트). y 가 아래로 향하므로 + 는 화면에서 시계방향
            var angle = 0.5 * Math.Atan2(2 * m.Mu11, m.Mu20 - m.Mu02) * 180 / Math.PI;
            if (angle <= -90) angle += 180;
            // 최소 회전 사각형 — 주축에 가까운 변이 길이
            var box = Cv2.MinAreaRect(c);
            double len = Math.Max(box.Size.Width, box.Size.Height), wid = Math.Min(box.Size.Width, box.Size.Height);
            list.Add(new Blob(area, center, bounds, c, angle, len, wid, toSource));
        }
        return [.. list.OrderByDescending(b => b.Area)];
    }

    /// <summary>Finds blobs with a parameter object.</summary>
    /// <param name="image">The image.</param>
    /// <param name="p">The parameters.</param>
    /// <returns>The blobs, largest first.</returns>
    public static Blob[] Find(VisionImage image, BlobParams p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return Find(image, p.Mode, p.Threshold, p.Invert, p.MinArea, p.MaxArea);
    }
}

/// <summary>Brightness measurements (presence checks, surface checks). Color images are converted to gray.</summary>
/// <example>
/// <code>
/// job.Add("tab", roi: () =&gt; roi, ctx =&gt;
/// {
///     ctx.Value("mean", Brightness.Mean(ctx.Image), min: 120, max: 220, code: "BRIGHT");
///     ctx.Value("dark pixels", Brightness.Count(ctx.Image, 0, 100), max: 200, code: "NOTCH");
/// });
/// </code>
/// </example>
public static class Brightness
{
    /// <summary>Mean brightness (0–255).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The mean.</returns>
    public static double Mean(VisionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var g = image.Gray();
        return Cv2.Mean(g.Mat).Val0;
    }

    /// <summary>Standard deviation of brightness — how uneven the surface is (scratches, stains, texture).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The standard deviation.</returns>
    public static double StdDev(VisionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var g = image.Gray();
        Cv2.MeanStdDev(g.Mat, out _, out var sd);
        return sd.Val0;
    }

    /// <summary>Number of pixels whose brightness is within [min, max].</summary>
    /// <param name="image">The image.</param>
    /// <param name="min">Lowest brightness (inclusive).</param>
    /// <param name="max">Highest brightness (inclusive).</param>
    /// <returns>The pixel count.</returns>
    public static int Count(VisionImage image, int min, int max)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var g = image.Gray();
        using var mask = new Mat();
        Cv2.InRange(g.Mat, new Scalar(min), new Scalar(max), mask);
        return Cv2.CountNonZero(mask);
    }
}
