using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>Shape of a <see cref="VisionRoi"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VisionRoiShape>))]
public enum VisionRoiShape
{
    /// <summary>Not set. A step whose ROI is not set ends in Error ("ROI is not set").</summary>
    None,

    /// <summary>Axis-aligned rectangle: <see cref="VisionRoi.X"/>, <see cref="VisionRoi.Y"/> = top-left corner.</summary>
    Rect,

    /// <summary>Rotated rectangle: <see cref="VisionRoi.X"/>, <see cref="VisionRoi.Y"/> = center, <see cref="VisionRoi.Angle"/> in degrees (clockwise on screen is +).</summary>
    Rotated,
}

/// <summary>
/// A region of interest of an inspection step, in the coordinates of the base (teaching) image. Saved as plain JSON with <c>System.Text.Json</c>.
/// </summary>
/// <remarks>
/// When the job has an alignment step (<c>pose:</c>), the ROI follows the part: it is moved and rotated by the difference between the current pose and the reference pose.
/// </remarks>
public readonly record struct VisionRoi
{
    /// <summary>The shape. <see cref="VisionRoiShape.None"/> (the default value) means not set.</summary>
    public VisionRoiShape Shape { get; init; }

    /// <summary>Rect: left edge. Rotated: center x.</summary>
    public double X { get; init; }

    /// <summary>Rect: top edge. Rotated: center y.</summary>
    public double Y { get; init; }

    /// <summary>Width in px.</summary>
    public double Width { get; init; }

    /// <summary>Height in px.</summary>
    public double Height { get; init; }

    /// <summary>Rotated only: angle in degrees, clockwise on screen is +. 0 for Rect.</summary>
    public double Angle { get; init; }

    /// <summary>An axis-aligned rectangle.</summary>
    /// <param name="x">Left edge (px).</param>
    /// <param name="y">Top edge (px).</param>
    /// <param name="width">Width (px).</param>
    /// <param name="height">Height (px).</param>
    /// <returns>The ROI.</returns>
    public static VisionRoi Rect(double x, double y, double width, double height)
        => new() { Shape = VisionRoiShape.Rect, X = x, Y = y, Width = width, Height = height };

    /// <summary>A rotated rectangle. Inside the step, the image is upright: its x axis runs along the rectangle's width.</summary>
    /// <param name="cx">Center x (px).</param>
    /// <param name="cy">Center y (px).</param>
    /// <param name="width">Width (px), along the angle.</param>
    /// <param name="height">Height (px).</param>
    /// <param name="angle">Angle in degrees, clockwise on screen is +.</param>
    /// <returns>The ROI.</returns>
    public static VisionRoi Rotated(double cx, double cy, double width, double height, double angle)
        => new() { Shape = VisionRoiShape.Rotated, X = cx, Y = cy, Width = width, Height = height, Angle = angle };

    /// <summary>Whether the ROI is not set (no shape or zero size).</summary>
    [JsonIgnore]
    public bool IsEmpty => Shape == VisionRoiShape.None || Width <= 0 || Height <= 0;

    /// <summary>The center point (px).</summary>
    [JsonIgnore]
    public Point2d Center => Shape == VisionRoiShape.Rotated ? new(X, Y) : new(X + Width / 2, Y + Height / 2);

    // 회전 각도 (Rect 는 0)
    internal double PlacedAngle => Shape == VisionRoiShape.Rotated ? Angle : 0;

    // 네 꼭짓점 (왼위 → 오른위 → 오른아래 → 왼아래)
    internal Point2d[] Corners() => CornersOf(Center, Width, Height, PlacedAngle);

    internal static Point2d[] CornersOf(Point2d c, double w, double h, double angle)
    {
        var rad = angle * Math.PI / 180;
        double cs = Math.Cos(rad), sn = Math.Sin(rad);
        Point2d P(double u, double v) => new(c.X + cs * u - sn * v, c.Y + sn * u + cs * v);
        return [P(-w / 2, -h / 2), P(w / 2, -h / 2), P(w / 2, h / 2), P(-w / 2, h / 2)];
    }
}
