using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// What a step receives (<c>ctx</c>): its image, cancellation, and the calls that record values, judgments, poses and drawings.
/// </summary>
/// <remarks>Thread-safe: a step may record from several tasks it started (for example with <c>Task.WhenAll</c>), as long as they finish before the step returns.</remarks>
public sealed class VisionContext
{
    private readonly JobRun _run;
    private readonly StepEntry _entry;
    private readonly Affine? _motion;
    private readonly string? _followed;
    private readonly object _gate = new();
    private readonly List<string> _ngCodes = [];
    private string? _fail;

    internal VisionContext(JobRun run, StepEntry entry, VisionImage image, Affine? motion, string? followed, CancellationToken cancel)
    {
        _run = run;
        _entry = entry;
        _motion = motion;
        _followed = followed;
        Image = image;
        Cancel = cancel;
    }

    /// <summary>The step name.</summary>
    public string Step => _entry.Step.Name;

    /// <summary>The step's image: its ROI cut out (upright, following the part's pose), or the whole image when the step has no ROI.</summary>
    public VisionImage Image { get; }

    /// <summary>Signaled when the step's time limit passes or the run is canceled. OpenCV calls cannot be interrupted; check it between long operations and pass it to awaited calls.</summary>
    public CancellationToken Cancel { get; }

    internal object? Setup => _run.Setup;
    internal object? Data => _run.Data;
    internal string? FailReason { get { lock (_gate) return _fail; } }
    internal IReadOnlyList<string> NgCodes { get { lock (_gate) return [.. _ngCodes]; } }

    // ── 판정 ─────────────────────────────────────────────

    /// <summary>Records a measured value and judges it against the limits. Out of range (or NaN) makes the step NG with <paramref name="code"/>.</summary>
    /// <param name="name">Value name.</param>
    /// <param name="value">The value.</param>
    /// <param name="min">Lower limit (inclusive), or <see langword="null"/>.</param>
    /// <param name="max">Upper limit (inclusive), or <see langword="null"/>.</param>
    /// <param name="code">NG code; <see langword="null"/> = the value name.</param>
    /// <param name="unit">Unit text for display, or <see langword="null"/>.</param>
    /// <returns>Whether the value is within the limits.</returns>
    public bool Value(string name, double value, double? min = null, double? max = null, string? code = null, string? unit = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var ok = !double.IsNaN(value) && (min is null || value >= min) && (max is null || value <= max);
        var c = code ?? name;
        lock (_gate)
        {
            _run.AddValue(new VisionValue(name, value, min, max, unit, c, ok, Step));
            if (!ok && !_ngCodes.Contains(c))
                _ngCodes.Add(c);
        }
        return ok;
    }

    /// <summary>Judges directly, without a value. <see langword="false"/> makes the step NG with <paramref name="code"/>.</summary>
    /// <param name="ok">The judgment.</param>
    /// <param name="code">NG code.</param>
    /// <returns><paramref name="ok"/>.</returns>
    public bool Judge(bool ok, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!ok)
            lock (_gate)
                if (!_ngCodes.Contains(code))
                    _ngCodes.Add(code);
        return ok;
    }

    /// <summary>Marks the step as unable to inspect (Error) — for example when an alignment mark is missing on a machine that should stop rather than reject.</summary>
    /// <param name="reason">Why.</param>
    public void Fail(string reason)
    {
        lock (_gate)
            _fail ??= reason ?? "Failed";
    }

    // ── 위치 보정 ────────────────────────────────────────

    /// <summary>Sets the part's pose. The name is the step's declared <c>pose:</c> (or the step name). Later steps' ROIs follow it.</summary>
    /// <param name="current">The pose found in this image.</param>
    /// <param name="reference">The pose on the base (teaching) image, from the setup. <see langword="null"/> = this image is the base (ROIs stay where they were drawn) — use it when teaching.</param>
    public void SetPose(VisionPose current, VisionPose? reference) => SetPose(_entry.Pose ?? Step, current, reference);

    /// <summary>Sets a named pose (when a job aligns more than one thing, e.g. body and label).</summary>
    /// <param name="name">Pose name.</param>
    /// <param name="current">The pose found in this image.</param>
    /// <param name="reference">The pose on the base image; <see langword="null"/> = this image is the base.</param>
    public void SetPose(string name, VisionPose current, VisionPose? reference)
    {
        ArgumentNullException.ThrowIfNull(name);
        _run.SetPose(name, current, reference ?? current, Step);
    }

    // ── 이미지 ───────────────────────────────────────────

    /// <summary>Cuts another region out of the run's image — for steps that look at several regions. The region follows the same pose as the step's ROI, and its border is drawn.</summary>
    /// <param name="roi">The region, in base image coordinates.</param>
    /// <returns>The region, upright (released at the end of the run).</returns>
    public VisionImage Crop(VisionRoi roi)
    {
        var img = _run.Place(roi, _motion);
        _run.AddRoiOverlay(roi, _motion, Step, _followed);
        return img;
    }

    /// <summary>Brings a Mat into the run: it is treated as having the same coordinates as <see cref="Image"/> and is released at the end of the run.
    /// The pixels are shared; your Mat is not disposed.</summary>
    /// <param name="mat">An 8-bit BGR or gray Mat.</param>
    /// <returns>The image.</returns>
    public VisionImage From(Mat mat)
    {
        ArgumentNullException.ThrowIfNull(mat);
        return Image.Derive(VisionImage.Share(mat), Affine.Identity);
    }

    // ── 그리기 (좌표는 원본으로 자동 환산) ───────────────

    /// <summary>Draws blobs (outlines and centers).</summary>
    /// <param name="blobs">The blobs.</param>
    public void Draw(IEnumerable<Blob> blobs)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        foreach (var b in blobs)
            Draw(b);
    }

    /// <summary>Draws a blob (outline and center).</summary>
    /// <param name="blob">The blob.</param>
    public void Draw(Blob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        Add(VisionOverlayKind.Contour, blob.Contour.Select(p => new Point2d(p.X, p.Y)), null, blob.ToSource);
        Add(VisionOverlayKind.Point, [blob.Center], null, blob.ToSource);
    }

    /// <summary>Draws a caliper result (the two edges and the line between them, with the distance).</summary>
    /// <param name="caliper">The result.</param>
    public void Draw(CaliperResult caliper)
    {
        ArgumentNullException.ThrowIfNull(caliper);
        if (!caliper.Found)
            return;
        Add(VisionOverlayKind.Line, [caliper.P1, caliper.P2], $"{caliper.Distance:0.00}", caliper.ToSource);
        Add(VisionOverlayKind.Point, [caliper.P1], null, caliper.ToSource);
        Add(VisionOverlayKind.Point, [caliper.P2], null, caliper.ToSource);
    }

    /// <summary>Draws the most unusual region of an anomaly result, with its score.</summary>
    /// <param name="anomaly">The result.</param>
    public void Draw(AnomalyResult anomaly)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        var r = anomaly.Region;
        Add(VisionOverlayKind.Box, [r.TopLeft, new(r.Right, r.Top), r.BottomRight, new(r.Left, r.Bottom)], $"{anomaly.Score:0.00}", anomaly.ToSource);
    }

    /// <summary>Draws a box.</summary>
    /// <param name="rect">The box in the coordinates of <paramref name="from"/>.</param>
    /// <param name="label">Text next to it, or <see langword="null"/>.</param>
    /// <param name="from">The image the coordinates belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    public void DrawBox(Rect2d rect, string? label = null, VisionImage? from = null)
        => Add(VisionOverlayKind.Box, [rect.TopLeft, new(rect.Right, rect.Top), rect.BottomRight, new(rect.Left, rect.Bottom)], label, (from ?? Image).ToSourceTransform);

    /// <summary>Draws a box.</summary>
    /// <param name="rect">The box in the coordinates of <paramref name="from"/>.</param>
    /// <param name="label">Text next to it, or <see langword="null"/>.</param>
    /// <param name="from">The image the coordinates belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    public void DrawBox(Rect rect, string? label = null, VisionImage? from = null)
        => DrawBox(new Rect2d(rect.X, rect.Y, rect.Width, rect.Height), label, from);

    /// <summary>Draws a line.</summary>
    /// <param name="p1">Start.</param>
    /// <param name="p2">End.</param>
    /// <param name="label">Text, or <see langword="null"/>.</param>
    /// <param name="from">The image the coordinates belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    public void DrawLine(Point2d p1, Point2d p2, string? label = null, VisionImage? from = null)
        => Add(VisionOverlayKind.Line, [p1, p2], label, (from ?? Image).ToSourceTransform);

    /// <summary>Draws a point.</summary>
    /// <param name="p">The point.</param>
    /// <param name="label">Text, or <see langword="null"/>.</param>
    /// <param name="from">The image the coordinates belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    public void DrawPoint(Point2d p, string? label = null, VisionImage? from = null)
        => Add(VisionOverlayKind.Point, [p], label, (from ?? Image).ToSourceTransform);

    /// <summary>Draws text.</summary>
    /// <param name="p">Where.</param>
    /// <param name="text">The text.</param>
    /// <param name="from">The image the coordinates belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    public void DrawText(Point2d p, string text, VisionImage? from = null)
        => Add(VisionOverlayKind.Text, [p], text, (from ?? Image).ToSourceTransform);

    /// <summary>Draws a closed outline.</summary>
    /// <param name="points">The outline points.</param>
    /// <param name="label">Text, or <see langword="null"/>.</param>
    /// <param name="from">The image the coordinates belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    public void DrawContour(IEnumerable<Point2d> points, string? label = null, VisionImage? from = null)
        => Add(VisionOverlayKind.Contour, points, label, (from ?? Image).ToSourceTransform);

    private void Add(VisionOverlayKind kind, IEnumerable<Point2d> points, string? label, Affine toSource)
        => _run.AddOverlay(new VisionOverlay(kind, points.Select(toSource.Map).ToArray(), label, Step));
}
