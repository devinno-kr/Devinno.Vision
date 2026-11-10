using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>Which brightness change the caliper looks for, scanning along the image's x axis.</summary>
public enum EdgePolarity
{
    /// <summary>Any change: the outermost edges on both sides.</summary>
    Both,

    /// <summary>A bright object on a dark background: dark→bright on the left, bright→dark on the right.</summary>
    BrightObject,

    /// <summary>A dark object on a bright background: bright→dark on the left, dark→bright on the right.</summary>
    DarkObject,
}

/// <summary>Result of <see cref="Edge.Caliper"/>: the two edges and the distance between them, in the coordinates of the image it ran on.</summary>
public sealed class CaliperResult
{
    internal CaliperResult(bool found, Point2d p1, Point2d p2, double contrast1, double contrast2, Affine toSource)
    {
        Found = found;
        P1 = p1;
        P2 = p2;
        Contrast1 = contrast1;
        Contrast2 = contrast2;
        ToSource = toSource;
    }

    /// <summary>Whether both edges were found.</summary>
    public bool Found { get; }

    /// <summary>The left edge (sub-pixel), on the image's middle row.</summary>
    public Point2d P1 { get; }

    /// <summary>The right edge (sub-pixel), on the image's middle row.</summary>
    public Point2d P2 { get; }

    /// <summary>Distance P1→P2 in px; <see cref="double.NaN"/> when not found (a NaN value makes <c>ctx.Value</c> NG).</summary>
    public double Distance => Found ? P2.X - P1.X : double.NaN;

    /// <summary>Edge strength at P1 (brightness change per px).</summary>
    public double Contrast1 { get; }

    /// <summary>Edge strength at P2.</summary>
    public double Contrast2 { get; }

    internal Affine ToSource { get; }
}

/// <summary>Edge measurement.</summary>
public static class Edge
{
    /// <summary>
    /// Finds the outermost pair of edges along the image's x axis and measures the distance between them (width, gap, length), to sub-pixel.
    /// The brightness is averaged over all rows, so put the ROI across the feature: its width along the direction to measure, its height along the edges.
    /// With a rotated ROI that follows the part, the measurement is along the part's axis.
    /// </summary>
    /// <param name="image">The image (usually <c>ctx.Image</c>, the step's ROI).</param>
    /// <param name="polarity">Which brightness change to look for.</param>
    /// <param name="minContrast">Weakest edge to accept (brightness change per px, after averaging).</param>
    /// <returns>The result; <see cref="CaliperResult.Found"/> is <see langword="false"/> when an edge is missing.</returns>
    public static CaliperResult Caliper(VisionImage image, EdgePolarity polarity = EdgePolarity.Both, double minContrast = 20)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var g = image.Gray();
        using var profile = new Mat();
        Cv2.Reduce(g.Mat, profile, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_64F);
        var n = profile.Cols;
        var y = (image.Height - 1) / 2.0;
        if (n < 5)
            return new CaliperResult(false, default, default, 0, 0, image.ToSourceTransform);

        var p = new double[n];
        for (var i = 0; i < n; i++)
            p[i] = profile.At<double>(0, i);
        // 잡음을 줄이려고 [1 2 1] 로 다듬은 뒤 중앙 차분
        var s = new double[n];
        for (var i = 0; i < n; i++)
            s[i] = (p[Math.Max(i - 1, 0)] + 2 * p[i] + p[Math.Min(i + 1, n - 1)]) / 4;
        var grad = new double[n];
        for (var i = 1; i < n - 1; i++)
            grad[i] = (s[i + 1] - s[i - 1]) / 2;

        int sign1 = polarity switch { EdgePolarity.BrightObject => 1, EdgePolarity.DarkObject => -1, _ => 0 };
        var left = Scan(grad, minContrast, sign1, fromLeft: true);
        var right = Scan(grad, minContrast, -sign1, fromLeft: false);
        if (left is null || right is null || right.Value.X <= left.Value.X)
            return new CaliperResult(false, left is { } l ? new Point2d(l.X, y) : default, right is { } r ? new Point2d(r.X, y) : default,
                left?.Contrast ?? 0, right?.Contrast ?? 0, image.ToSourceTransform);
        return new CaliperResult(true, new Point2d(left.Value.X, y), new Point2d(right.Value.X, y), left.Value.Contrast, right.Value.Contrast, image.ToSourceTransform);
    }

    // 한쪽 끝부터 문턱을 넘는 첫 에지를 찾고, 그 봉우리에서 포물선으로 서브픽셀 위치
    private static (double X, double Contrast)? Scan(double[] grad, double min, int sign, bool fromLeft)
    {
        var n = grad.Length;
        double Mag(int i) => sign == 0 ? Math.Abs(grad[i]) : sign * grad[i];
        var i0 = fromLeft ? 1 : n - 2;
        var step = fromLeft ? 1 : -1;
        for (var i = i0; i >= 1 && i <= n - 2; i += step)
        {
            if (Mag(i) < min)
                continue;
            // 같은 에지 안에서 봉우리까지 올라간다 (부호 그대로)
            var k = i;
            var dir = sign == 0 ? Math.Sign(grad[i]) : sign;
            while (k + step >= 1 && k + step <= n - 2 && dir * grad[k + step] > dir * grad[k])
                k += step;
            double a = dir * grad[k - 1], b = dir * grad[k], c = dir * grad[k + 1];
            var den = a - 2 * b + c;
            var off = Math.Abs(den) > 1e-12 ? Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0;
            return (k + off, b);
        }
        return null;
    }
}
