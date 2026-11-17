using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// What a step receives (<c>ctx</c>): its image, cancellation, and the calls that record values, judgments, poses and drawings.
/// </summary>
/// <remarks>
/// <para>Thread-safe: a step may record from several tasks it started (for example with <c>Task.WhenAll</c>), as long as they finish before the step returns.</para>
/// <para>A step judges only through its context: <see cref="Value(string, double, double?, double?, string?, string?)"/> for a measured value,
/// <see cref="Judge"/> for a direct judgment, <see cref="Fail"/> when it cannot inspect. A step that records nothing is OK; a step that throws is Error.</para>
/// <para>Images made inside the step (<see cref="Image"/>, <see cref="Crop"/>, <see cref="From"/>, and images derived from them) are released when the run ends.
/// A context is valid only while its step runs; do not keep it.</para>
/// </remarks>
/// <example>
/// <code>
/// job.Add("left hole", roi: () =&gt; VisionRoi.Rect(194, 251, 60, 60), ctx =&gt;
/// {
///     var holes = Blob.Find(ctx.Image, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
///     ctx.Draw(holes);                                                       // drawn at the right place on the original image
///     ctx.Value("left holes", holes.Length, min: 1, max: 1, code: "HOLE");   // out of range → the step is NG with code HOLE
/// });
/// </code>
/// </example>
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
    /// <remarks>
    /// The value is always recorded (<see cref="VisionResult.Values"/>), good or bad, so it can be shown and logged. Without limits it is recorded only.
    /// Several values in one step give several codes; each code appears once in <see cref="VisionResult.Codes"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// ctx.Value("holes", blobs.Length, min: 4, max: 4, code: "HOLE");
    /// ctx.Value("dark pixels", Brightness.Count(ctx.Image, 0, 100), max: 200, code: "DISCOLOR");
    /// ctx.Value("mean", Brightness.Mean(ctx.Image));                          // recorded, not judged
    /// </code>
    /// </example>
    public bool Value(string name, double value, double? min = null, double? max = null, string? code = null, string? unit = null)
        => Record(name, value, min, max, code, unit, null);

    private bool Record(string name, double value, double? min, double? max, string? code, string? unit, double? nominal)
    {
        ArgumentNullException.ThrowIfNull(name);
        var ok = !double.IsNaN(value) && (min is null || value >= min) && (max is null || value <= max);
        var c = code ?? name;
        lock (_gate)
        {
            _run.AddValue(new VisionValue(name, value, min, max, unit, c, ok, Step, nominal));
            if (!ok && !_ngCodes.Contains(c))
                _ngCodes.Add(c);
        }
        return ok;
    }

    /// <summary>Records a measured value against a nominal value with tolerances (drawing style: 4.15 +0.05 / −0.05). The result keeps the nominal, so the deviation can be shown and logged.</summary>
    /// <param name="name">Value name.</param>
    /// <param name="value">The value.</param>
    /// <param name="nominal">The nominal (target) value.</param>
    /// <param name="plus">Upper tolerance (≥ 0): the value may be up to <c>nominal + plus</c>.</param>
    /// <param name="minus">Lower tolerance (≥ 0): the value may be down to <c>nominal − minus</c>.</param>
    /// <param name="code">NG code; <see langword="null"/> = the value name.</param>
    /// <param name="unit">Unit text for display, or <see langword="null"/>.</param>
    /// <returns>Whether the value is within the tolerance.</returns>
    /// <example>
    /// <code>
    /// var e = Edge.Caliper(ctx.Image);
    /// ctx.Value("width", ctx.Mm(e), nominal: 90, plus: 1, minus: 1, code: "WIDTH", unit: "mm");   // OK from 89 to 91
    /// </code>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plus"/> or <paramref name="minus"/> is negative.</exception>
    public bool Value(string name, double value, double nominal, double plus, double minus, string? code = null, string? unit = null)
    {
        if (plus < 0 || minus < 0)
            throw new ArgumentOutOfRangeException(nameof(plus), "Tolerances are given as positive amounts (plus ≥ 0, minus ≥ 0).");
        return Record(name, value, nominal - minus, nominal + plus, code, unit, nominal);
    }

    // ── 단위 환산 ─────────────────────────────────────────

    /// <summary>Distance between two points in millimetres, using the job's <c>Calibration</c>. The points are converted to the original image first, so rotation, crop and resize do not matter.</summary>
    /// <param name="p1">First point.</param>
    /// <param name="p2">Second point.</param>
    /// <param name="from">The image the points belong to; <see langword="null"/> = <see cref="Image"/>.</param>
    /// <returns>The distance in mm.</returns>
    /// <exception cref="InvalidOperationException">The job has no calibration (the step becomes Error).</exception>
    public double Mm(Point2d p1, Point2d p2, VisionImage? from = null)
    {
        var t = (from ?? Image).ToSourceTransform;
        return MmBetween(t.Map(p1), t.Map(p2));
    }

    /// <summary>The caliper distance in millimetres; <see cref="double.NaN"/> when the edges were not found (a NaN value is NG in <c>ctx.Value</c>).</summary>
    /// <param name="caliper">The caliper result.</param>
    /// <returns>The distance in mm.</returns>
    /// <exception cref="InvalidOperationException">The job has no calibration (the step becomes Error).</exception>
    public double Mm(CaliperResult caliper)
    {
        ArgumentNullException.ThrowIfNull(caliper);
        if (_run.Calibration is null)
            throw NoCalibration();
        return caliper.Found ? MmBetween(caliper.ToSource.Map(caliper.P1), caliper.ToSource.Map(caliper.P2)) : double.NaN;
    }

    private double MmBetween(Point2d a, Point2d b)
    {
        var cal = _run.Calibration ?? throw NoCalibration();
        return cal.ToMm(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
    }

    private static InvalidOperationException NoCalibration()
        => new("The job has no calibration. Set job.Calibration (for example VisionCalibration.FromLength(px, mm)) before using ctx.Mm.");

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
    /// <param name="reason">Why. It becomes <see cref="StepResult.Reason"/> and, as "job/step: reason", <see cref="VisionResult.Error"/>.</param>
    /// <remarks>The step keeps running after this call; return from it yourself. The first reason given wins. The following steps still run.</remarks>
    public void Fail(string reason)
    {
        lock (_gate)
            _fail ??= reason ?? "Failed";
    }

    // ── 위치 보정 ────────────────────────────────────────

    /// <summary>Sets the part's pose. The name is the step's declared <c>pose:</c> (or the step name). Later steps' ROIs follow it.</summary>
    /// <param name="current">The pose found in this image.</param>
    /// <param name="reference">The pose on the base (teaching) image, from the setup. <see langword="null"/> = this image is the base (ROIs stay where they were drawn) — use it when teaching.</param>
    /// <remarks>
    /// The pose is also stored in <see cref="VisionResult.Poses"/>; when teaching, run the job on the base image with <paramref name="reference"/> = <see langword="null"/>
    /// and save that pose as the reference. A step that should set a pose but does not (for example the part is missing) leaves the steps that follow it Skipped.
    /// </remarks>
    /// <example>
    /// <code>
    /// job.Add("align", pose: "body", (ctx, s) =&gt;
    /// {
    ///     var part = Blob.Find(ctx.Image, s.Body).FirstOrDefault();
    ///     if (part is null) { ctx.Fail("part not found"); return; }
    ///     ctx.SetPose(VisionPose.FromBlob(part, s.BodyRef), s.BodyRef);
    /// });
    /// </code>
    /// </example>
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
    /// <exception cref="ArgumentException">The ROI is not set.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Less than half of the ROI is inside the image.</exception>
    public VisionImage Crop(VisionRoi roi)
    {
        var img = _run.Place(roi, _motion);
        _run.AddRoiOverlay(roi, _motion, Step);
        return img;
    }

    /// <summary>Brings a Mat into the run: it is treated as having the same coordinates as <see cref="Image"/> and is released at the end of the run.
    /// The pixels are shared; your Mat is not disposed.</summary>
    /// <param name="mat">An 8-bit BGR or gray Mat.</param>
    /// <returns>The image.</returns>
    /// <example>
    /// <code>
    /// using var edges = new Mat();                 // your Mat: you dispose it
    /// Cv2.Canny(ctx.Image.Mat, edges, 50, 150);
    /// var img = ctx.From(edges);                   // same coordinates as ctx.Image
    /// ctx.DrawBox(new Rect(10, 10, 20, 20), "here", from: img);
    /// </code>
    /// </example>
    /// <exception cref="NotSupportedException">The Mat is not 8-bit BGR or 8-bit gray.</exception>
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

    /// <summary>
    /// Draws a caliper result (the two edges and the line between them, with the distance).
    /// The label carries its unit: mm when the job has a calibration ("89.95 mm"), otherwise px ("410.75 px") — the same unit the length is usually judged in.
    /// </summary>
    /// <param name="caliper">The result.</param>
    public void Draw(CaliperResult caliper)
    {
        ArgumentNullException.ThrowIfNull(caliper);
        if (!caliper.Found)
            return;
        var label = _run.Calibration is null ? $"{caliper.Distance:0.00} px" : $"{Mm(caliper):0.00} mm";
        Add(VisionOverlayKind.Line, [caliper.P1, caliper.P2], label, caliper.ToSource);
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
