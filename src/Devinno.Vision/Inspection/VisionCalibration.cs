namespace Devinno.Vision;

/// <summary>
/// Pixel → millimetre conversion of a camera setup (a machine value, kept apart from the product setup: re-teaching a product must not change it,
/// and re-mounting the camera must not require re-teaching every product). Set it on the job: <c>job.Calibration = …</c>; use it in steps with <c>ctx.Mm</c>.
/// </summary>
/// <param name="MmPerPx">Millimetres per pixel of the original image.</param>
/// <remarks>A single scale assumes the camera looks straight at a flat part and the lens has little distortion. Lens distortion and perspective are not corrected.</remarks>
/// <example>
/// <code>
/// job.Calibration = VisionCalibration.FromLength(px: 400.0, mm: 100.0);   // a gauge of 100 mm measured as 400.0 px
///
/// // in a step
/// var e = Edge.Caliper(ctx.Image);
/// ctx.Value("width", ctx.Mm(e), nominal: 100, plus: 1, minus: 1, code: "WIDTH", unit: "mm");
/// </code>
/// </example>
public sealed record VisionCalibration(double MmPerPx)
{
    /// <summary>A calibration from a known scale.</summary>
    /// <param name="mmPerPx">Millimetres per pixel.</param>
    /// <returns>The calibration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The scale is not positive.</exception>
    public static VisionCalibration Scale(double mmPerPx)
    {
        if (!(mmPerPx > 0) || double.IsInfinity(mmPerPx))
            throw new ArgumentOutOfRangeException(nameof(mmPerPx), $"mm per px must be positive (got {mmPerPx}).");
        return new VisionCalibration(mmPerPx);
    }

    /// <summary>A calibration from a gauge of known length measured in the image (for example a gauge block measured with <c>Edge.Caliper</c>).</summary>
    /// <param name="px">The measured length in pixels of the original image.</param>
    /// <param name="mm">The real length in millimetres.</param>
    /// <returns>The calibration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A length is not positive.</exception>
    public static VisionCalibration FromLength(double px, double mm)
    {
        if (!(px > 0) || !(mm > 0))
            throw new ArgumentOutOfRangeException(nameof(px), $"Both lengths must be positive (got {px} px, {mm} mm).");
        return Scale(mm / px);
    }

    /// <summary>Converts a length in pixels of the original image to millimetres.</summary>
    /// <param name="px">Length in pixels.</param>
    /// <returns>Length in millimetres.</returns>
    public double ToMm(double px) => px * MmPerPx;
}
