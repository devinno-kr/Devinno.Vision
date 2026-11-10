using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// Alignment for round parts that can lie at any angle (nuts, washers, caps, gears): the brightness around a center is unrolled into an angular signature,
/// and the rotation is found by matching it to the signature of a good part. A part lying upside down (mirrored) is reported too.
/// </summary>
/// <remarks>
/// <para>Blob alignment cannot do this: a part with several identical lobes has no long axis. Find the center first (usually the hole: a dark blob near the middle),
/// then <see cref="Find"/> gives the pose; pass it to <c>ctx.SetPose(match.Pose, model.ReferencePose)</c> and the ROIs follow the rotation.</para>
/// <para>Saved as JSON (<see cref="Save"/> / <see cref="Load"/>, or as a setup property).</para>
/// </remarks>
public sealed class RoundModel
{
    /// <summary>Angular resolution of the signature: 720 steps = 0.5°.</summary>
    public const int Steps = 720;

    /// <summary>Center x on the base image (px).</summary>
    public double CenterX { get; init; }

    /// <summary>Center y on the base image (px).</summary>
    public double CenterY { get; init; }

    /// <summary>Inner radius of the band used for the signature (px).</summary>
    public double InnerRadius { get; init; }

    /// <summary>Outer radius of the band (px). Put the band where the part's shape varies with angle (lobes, notches).</summary>
    public double OuterRadius { get; init; }

    /// <summary>The signature of the good part (mean brightness per angle, mean removed).</summary>
    public double[] Profile { get; init; } = [];

    /// <summary>The reference pose: the base image's center at angle 0. Pass it as <c>reference</c> to <c>ctx.SetPose</c>.</summary>
    [JsonIgnore]
    public VisionPose ReferencePose => new(CenterX, CenterY, 0);

    /// <summary>Learns the signature from a good part.</summary>
    /// <param name="image">The base image (the ROIs are drawn on it).</param>
    /// <param name="center">The part's center in <paramref name="image"/> (px).</param>
    /// <param name="innerRadius">Inner radius of the band (px).</param>
    /// <param name="outerRadius">Outer radius of the band (px).</param>
    /// <returns>The model.</returns>
    public static RoundModel Train(VisionImage image, Point2d center, double innerRadius, double outerRadius)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (innerRadius < 0 || outerRadius <= innerRadius + 1)
            throw new ArgumentException($"The band needs 0 ≤ inner < outer (got {innerRadius:0.#}–{outerRadius:0.#}).");
        var src = image.ToSource(center);
        return new RoundModel
        {
            CenterX = src.X,
            CenterY = src.Y,
            InnerRadius = innerRadius,
            OuterRadius = outerRadius,
            Profile = ProfileOf(image, center, innerRadius, outerRadius),
        };
    }

    /// <summary>Finds the rotation of the part around <paramref name="center"/>, and how well it matches as is and mirrored.</summary>
    /// <param name="image">The image.</param>
    /// <param name="center">The part's center in <paramref name="image"/> (px).</param>
    /// <returns>The match: pose in the original image, score, mirror score.</returns>
    public RoundMatch Find(VisionImage image, Point2d center)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Profile.Length != Steps)
            throw new InvalidOperationException("The model is not trained.");
        var p = ProfileOf(image, center, InnerRadius, OuterRadius);
        var (shift, score) = Best(p, Profile);
        // 거울상: 세로축 대칭이면 각도 θ → 180° − θ
        var m = new double[Steps];
        for (var i = 0; i < Steps; i++)
            m[i] = p[((Steps / 2 - i) % Steps + Steps) % Steps];
        var (_, mirror) = Best(m, Profile);

        var t = image.ToSourceTransform;
        var c = t.Map(center);
        var angle = shift * 360.0 / Steps + Math.Atan2(t.D, t.A) * 180 / Math.PI;
        return new RoundMatch(new VisionPose(c.X, c.Y, VisionPose.Normalize(angle)), score, mirror);
    }

    /// <summary>Saves the model as JSON.</summary>
    /// <param name="path">The file path.</param>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    /// <summary>Loads a model saved with <see cref="Save"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The model.</returns>
    public static RoundModel Load(string path) => JsonSerializer.Deserialize<RoundModel>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Not a RoundModel: {path}");

    // 극좌표로 펼쳐 (행 = 각도, 화면에서 시계방향으로 증가) 대역의 평균 밝기
    private static double[] ProfileOf(VisionImage image, Point2d center, double inner, double outer)
    {
        using var gray = image.Gray();
        var cols = Math.Max(2, (int)Math.Ceiling(outer));
        using var polar = new Mat();
        Cv2.WarpPolar(gray.Mat, polar, new Size(cols, Steps), new Point2f((float)center.X, (float)center.Y), outer, InterpolationFlags.Linear, WarpPolarMode.Linear);
        var c0 = Math.Clamp((int)inner, 0, cols - 1);
        using var red = new Mat();
        Cv2.Reduce(polar[new Rect(c0, 0, cols - c0, Steps)], red, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_64F);
        var v = new double[Steps];
        for (var i = 0; i < Steps; i++)
            v[i] = red.At<double>(i, 0);
        var mean = v.Average();
        for (var i = 0; i < Steps; i++)
            v[i] -= mean;
        return v;
    }

    // 원형 상관이 가장 큰 이동 (포물선으로 반 칸 아래까지)
    private static (double Shift, double Score) Best(double[] p, double[] r)
    {
        var norm = Math.Sqrt(p.Sum(x => x * x) * r.Sum(x => x * x));
        if (norm < 1e-9)
            return (0, 0);
        var corr = new double[Steps];
        for (var s = 0; s < Steps; s++)
        {
            double c = 0;
            for (var i = 0; i < Steps; i++)
                c += p[(i + s) % Steps] * r[i];
            corr[s] = c / norm;
        }
        var best = 0;
        for (var s = 1; s < Steps; s++)
            if (corr[s] > corr[best])
                best = s;
        double a = corr[(best + Steps - 1) % Steps], b = corr[best], c2 = corr[(best + 1) % Steps];
        var den = a - 2 * b + c2;
        var off = Math.Abs(den) > 1e-12 ? Math.Clamp(0.5 * (a - c2) / den, -0.5, 0.5) : 0;
        return (best + off, b);
    }
}

/// <summary>Result of <see cref="RoundModel.Find"/>.</summary>
/// <param name="Pose">The part's center and rotation in the original image (angle relative to the base image).</param>
/// <param name="Score">Match of the signature as is (−1…1; a good part is close to 1).</param>
/// <param name="MirrorScore">Match of the mirrored signature. Higher than <paramref name="Score"/> = the part lies upside down.</param>
public sealed record RoundMatch(VisionPose Pose, double Score, double MirrorScore)
{
    /// <summary>Whether the mirrored signature matches better (the part lies upside down).</summary>
    public bool Mirrored => MirrorScore > Score;
}
