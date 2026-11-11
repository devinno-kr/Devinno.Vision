using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// Where the part's reference point is and which way the part faces, in the original image. Saved in setups, process data and result JSON.
/// </summary>
/// <param name="X">Reference point x (px).</param>
/// <param name="Y">Reference point y (px).</param>
/// <param name="Angle">Direction in degrees, clockwise on screen is +, range (−180, 180].</param>
/// <remarks>
/// The reference pose is the pose found on the base (teaching) image; the ROIs are drawn on that image.
/// At run time the ROIs move by the difference: <c>p' = current.origin + R(current.Angle − reference.Angle)·(p − reference.origin)</c>.
/// </remarks>
/// <example>
/// Teach the reference pose on the base image, then use it:
/// <code>
/// setup.BodyRef = null;                                        // this image is the base
/// using (var r = await job.RunAsync(baseImage))
///     if (r.Poses.TryGetValue("body", out var pose))
///         setup.BodyRef = pose;                                // save the setup afterwards
///
/// // in the alignment step
/// ctx.SetPose(VisionPose.FromBlob(part, s.BodyRef), s.BodyRef);
/// </code>
/// </example>
public readonly record struct VisionPose(double X, double Y, double Angle)
{
    /// <summary>Pose from a blob: its center and the direction of its long axis.</summary>
    /// <param name="blob">The blob (usually the whole part).</param>
    /// <param name="reference">The reference pose. A long axis has two directions (0° and 180° look the same); the one closer to the reference is chosen.
    /// <see langword="null"/> = closer to 0°. Do not use blob alignment where the part can turn more than 90°, or for nearly round or square parts.</param>
    /// <returns>The pose in the original image.</returns>
    public static VisionPose FromBlob(Blob blob, VisionPose? reference = null)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var center = blob.ToSource.Map(blob.Center);
        // 주축 방향 벡터를 원본 좌표로 (회전 크롭 안에서 찾은 블롭도 맞게)
        var rad = blob.Angle * Math.PI / 180;
        var dir = blob.ToSource.MapVector(new Point2d(Math.Cos(rad), Math.Sin(rad)));
        var angle = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
        var target = reference?.Angle ?? 0;
        if (Math.Abs(Normalize(angle - target)) > 90)
            angle += 180;
        return new VisionPose(center.X, center.Y, Normalize(angle));
    }

    // (−180, 180]
    internal static double Normalize(double deg)
    {
        deg %= 360;
        if (deg <= -180) deg += 360;
        else if (deg > 180) deg -= 360;
        return deg;
    }

    // 기준 → 현재 로 옮기는 변환 (원본 좌표 안에서)
    internal static Affine Motion(VisionPose reference, VisionPose current)
    {
        var rad = Normalize(current.Angle - reference.Angle) * Math.PI / 180;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        // p' = cur + R·(p − ref)
        return new Affine(c, -s, current.X - (c * reference.X - s * reference.Y),
                          s, c, current.Y - (s * reference.X + c * reference.Y));
    }
}
