using System.Text.Json;

namespace Devinno.Vision;

/// <summary>
/// The result of one job run: judgment, NG codes, values, step states, poses and overlays. Every run returns one, whatever happens.
/// </summary>
/// <remarks>
/// Receive it with <c>using</c>. It borrows the image given to the run (<see cref="Image"/>), so dispose it before that image —
/// <c>using</c> order does this naturally. Step images (<see cref="StepResult.Image"/>) belong to the result.
/// </remarks>
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
    public VisionState State { get; internal set; }

    /// <summary>Shorthand for <c>State == VisionState.Ok</c>.</summary>
    public bool Ok => State == VisionState.Ok;

    /// <summary>NG codes, without duplicates, in the order they occurred.</summary>
    public IReadOnlyList<string> Codes => CodeList;

    /// <summary>Measured values with their limits.</summary>
    public IReadOnlyList<VisionValue> Values => ValueList;

    /// <summary>Why the run is Error (<c>"job/step: reason"</c>), otherwise <see langword="null"/>.</summary>
    public string? Error { get; internal set; }

    /// <summary>Step results.</summary>
    public StepResultList Steps { get; } = new();

    /// <summary>Names of disabled steps. Disabled steps do not affect the judgment — so an inspection could pass without checking; show this.</summary>
    public IReadOnlyList<string> Disabled => DisabledList;

    /// <summary>Poses set in this run, by name.</summary>
    public IReadOnlyDictionary<string, VisionPose> Poses => PoseMap;

    /// <summary>The inspected image (borrowed from the caller).</summary>
    /// <exception cref="ObjectDisposedException">The result has been disposed.</exception>
    public VisionImage Image => _disposed ? throw new ObjectDisposedException(nameof(VisionResult)) : _image ?? throw new InvalidOperationException("This result has no image.");

    /// <summary>Shapes to draw, in the inspected image's coordinates.</summary>
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
            values = Values.Select(v => new { name = v.Name, value = v.Value, min = v.Min, max = v.Max, unit = v.Unit, code = v.Code, ok = v.Ok, step = v.Step }),
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
