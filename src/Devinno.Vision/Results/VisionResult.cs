using System.Text.Json;

namespace Devinno.Vision;

/// <summary>
/// The result of one job run: judgment, NG codes, values, step states, poses and overlays. Every run returns one, whatever happens.
/// </summary>
/// <remarks>
/// <para>Receive it with <c>using</c>. It borrows the image given to the run (<see cref="Image"/>), so dispose it before that image —
/// <c>using</c> order does this naturally. Step images (<see cref="StepResult.Image"/>) belong to the result.</para>
/// <para>How the judgment is formed: an Error in a judging step, a timeout or a cancel makes the result Error (even when another step is NG);
/// otherwise any NG makes it NG; otherwise a step skipped for a missing pose, or no step run at all, makes it Error; otherwise it is OK.
/// An inspection that did not inspect never comes out OK.</para>
/// <para>After the run has returned, the result is not changed any more and can be read from any thread.</para>
/// </remarks>
/// <example>
/// <code>
/// using var img = VisionImage.Load(path);
/// using var r = await job.RunAsync(img);
///
/// if (r.State == VisionState.Ng)
///     foreach (var v in r.Values.Where(v =&gt; !v.Ok))
///         Console.WriteLine($"{v.Step}: {v.Name} = {v.Value:0.##} (limits {v.Min}–{v.Max}) → {v.Code}");
/// if (r.Disabled.Count &gt; 0)
///     Console.WriteLine("not checked: " + string.Join(", ", r.Disabled));
/// File.AppendAllText(logFile, r.ToJson() + Environment.NewLine);     // where results go is the app's decision
/// </code>
/// </example>
public sealed class VisionResult : IDisposable
{
    private readonly VisionImage? _image;
    private bool _disposed;

    internal VisionResult(string job, VisionImage? image)
    {
        Job = job;
        _image = image;
    }

    /// <summary>The job name.</summary>
    public string Job { get; }

    /// <summary>The judgment.</summary>
    /// <remarks>Handle all three values. <see cref="VisionState.Error"/> means the part was not inspected (see <see cref="Error"/>) — it is neither good nor a defect.</remarks>
    public VisionState State { get; internal set; }

    /// <summary>Shorthand for <c>State == VisionState.Ok</c>.</summary>
    public bool Ok => State == VisionState.Ok;

    /// <summary>NG codes, without duplicates, in the order they occurred.</summary>
    /// <remarks>
    /// A code is the <c>code</c> a step passed to <c>ctx.Value</c> or <c>ctx.Judge</c> (the value name when none was given). Branch on codes, not on value names or reason texts.
    /// Empty unless the judgment is NG. The codes of reason steps are added only to an NG result.
    /// </remarks>
    public IReadOnlyList<string> Codes => CodeList;

    /// <summary>Measured values with their limits.</summary>
    /// <remarks>Every <c>ctx.Value</c> call of every step that ran, in the order recorded, good and bad alike. <see cref="VisionValue.Ok"/> tells which ones failed; <see cref="VisionValue.Step"/> which step recorded it.</remarks>
    public IReadOnlyList<VisionValue> Values => ValueList;

    /// <summary>Why the run is Error (<c>"job/step: reason"</c>, the first step that failed; <c>"Busy"</c> when the job was still running), otherwise <see langword="null"/>.</summary>
    public string? Error { get; internal set; }

    /// <summary>Step results, one per step in the order the steps were added, also looked up by name (<c>r.Steps["Holes"]</c>).</summary>
    public StepResultList Steps { get; } = new();

    /// <summary>Names of disabled steps. Disabled steps do not affect the judgment — so an inspection could pass without checking; show this.</summary>
    public IReadOnlyList<string> Disabled => DisabledList;

    /// <summary>Poses set in this run, by name.</summary>
    public IReadOnlyDictionary<string, VisionPose> Poses => PoseMap;

    /// <summary>The inspected image (borrowed from the caller). Do not dispose it, and do not use it after the caller's image is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The result has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The result has no image.</exception>
    public VisionImage Image => _disposed ? throw new ObjectDisposedException(nameof(VisionResult)) : _image ?? throw new InvalidOperationException("This result has no image.");

    /// <summary>Shapes to draw, in the inspected image's coordinates.</summary>
    /// <remarks>
    /// The ROI border of every step that ran (<see cref="VisionOverlayKind.Roi"/>) and everything the steps drew with <c>ctx.Draw…</c>.
    /// The points are already converted to the original image, whatever crop, rotation or resize the step worked on.
    /// <see cref="VisionOverlay.Step"/> names the step, so a shape can be colored by <c>r.Steps[o.Step].State</c>.
    /// </remarks>
    /// <example>
    /// Draw the overlays on a copy of the image with OpenCV:
    /// <code>
    /// using var canvas = r.Image.Mat.Clone();
    /// foreach (var o in r.Overlays)
    /// {
    ///     var pts = o.Points.Select(p =&gt; new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
    ///     var color = r.Steps[o.Step].State == StepState.Ok ? new Scalar(0, 200, 0) : new Scalar(0, 0, 255);
    ///     if (pts.Length &gt;= 3) Cv2.Polylines(canvas, new[] { pts }, isClosed: true, color, thickness: 2);
    ///     else if (pts.Length == 2) Cv2.Line(canvas, pts[0], pts[1], color, thickness: 2);
    ///     else Cv2.Circle(canvas, pts[0], 4, color, thickness: -1);
    /// }
    /// Cv2.ImWrite("result.png", canvas);
    /// </code>
    /// </example>
    public IReadOnlyList<VisionOverlay> Overlays => OverlayList;

    /// <summary>Total run time.</summary>
    public TimeSpan Elapsed { get; internal set; }

    internal List<string> CodeList { get; } = [];
    internal List<VisionValue> ValueList { get; } = [];
    internal List<string> DisabledList { get; } = [];
    internal Dictionary<string, VisionPose> PoseMap { get; } = [];
    internal List<VisionOverlay> OverlayList { get; } = [];
    internal bool HasImage => _image is not null && !_disposed;

    /// <summary>The worst judgment of several results (Error &gt; NG &gt; OK) — for a part inspected with several jobs.</summary>
    /// <param name="results">The results.</param>
    /// <returns>The overall judgment; Error when no result is given.</returns>
    /// <example>
    /// <code>
    /// using var top = await topJob.RunAsync(topImage, data);
    /// using var side = await sideJob.RunAsync(sideImage, data);
    /// var part = VisionResult.Worst(top, side);
    /// </code>
    /// </example>
    public static VisionState Worst(params VisionResult[] results)
    {
        if (results is null || results.Length == 0)
            return VisionState.Error;
        return results.Max(r => r.State);
    }

    /// <summary>The result as JSON (no image): judgment, codes, error, values with limits, step states and times, poses, disabled steps, overlays.
    /// Devinno.Vision defines the format; when and where to save it is up to the app.</summary>
    /// <param name="indented">Pretty-print.</param>
    /// <returns>The JSON text.</returns>
    /// <remarks>
    /// Properties: <c>job</c>, <c>state</c> ("Ok", "Ng", "Error"), <c>codes</c>, <c>error</c>, <c>elapsedMs</c>, <c>time</c> (capture time of the image),
    /// <c>values</c> (<c>name</c>, <c>value</c>, <c>min</c>, <c>max</c>, <c>nominal</c>, <c>deviation</c>, <c>unit</c>, <c>code</c>, <c>ok</c>, <c>step</c>),
    /// <c>steps</c> (<c>name</c>, <c>state</c>, <c>reason</c>, <c>elapsedMs</c>, <c>followed</c>), <c>poses</c> (name → <c>x</c>, <c>y</c>, <c>angle</c>),
    /// <c>disabled</c>, <c>overlays</c> (<c>kind</c>, <c>points</c> as [x, y] pairs, <c>label</c>, <c>step</c>). Text that is not ASCII is written as it is, not escaped.
    /// </remarks>
    public string ToJson(bool indented = false)
    {
        var dto = new
        {
            job = Job,
            state = State.ToString(),
            codes = Codes,
            error = Error,
            elapsedMs = Math.Round(Elapsed.TotalMilliseconds, 1),
            time = _image?.Info.Time,
            values = Values.Select(v => new { name = v.Name, value = v.Value, min = v.Min, max = v.Max, nominal = v.Nominal, deviation = v.Deviation, unit = v.Unit, code = v.Code, ok = v.Ok, step = v.Step }),
            steps = Steps.Select(s => new { name = s.Name, state = s.State.ToString(), reason = s.Reason, elapsedMs = Math.Round(s.Elapsed.TotalMilliseconds, 1), followed = s.Followed }),
            poses = Poses.ToDictionary(p => p.Key, p => new { x = p.Value.X, y = p.Value.Y, angle = p.Value.Angle }),
            disabled = Disabled,
            overlays = Overlays.Select(o => new { kind = o.Kind.ToString(), points = o.Points.Select(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }), label = o.Label, step = o.Step }),
        };
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Releases the step images. The inspected image is not touched (it belongs to the caller).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var s in Steps)
            s.Image?.Release(Job);   // 실행 범위에서 넘겨받은 것 — 같은 안내 문구로 해제
    }
}
