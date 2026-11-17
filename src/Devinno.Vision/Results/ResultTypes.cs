using System.Collections;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>Judgment of a whole job run.</summary>
/// <remarks>The values start at 1: <c>default(VisionState)</c> is none of them, so a state that was never set is not <see cref="Ok"/>. A worse state has a larger value.</remarks>
public enum VisionState
{
    /// <summary>Inspected and good.</summary>
    Ok = 1,

    /// <summary>Inspected and defective (<see cref="VisionResult.Codes"/> says why).</summary>
    Ng = 2,

    /// <summary>Could not inspect: exception, timeout, alignment missing, busy, … (<see cref="VisionResult.Error"/> says why).</summary>
    Error = 3,
}

/// <summary>State of one step.</summary>
/// <remarks>The values start at 1: <c>default(StepState)</c> is none of them, so a state that was never set is not <see cref="Ok"/>.</remarks>
public enum StepState
{
    /// <summary>Ran and good.</summary>
    Ok = 1,

    /// <summary>Ran and defective.</summary>
    Ng = 2,

    /// <summary>Could not inspect (exception, <c>ctx.Fail</c>, timeout, ROI not set, …).</summary>
    Error = 3,

    /// <summary>Did not run: disabled, its pose is missing, after a timeout, or after NG with <see cref="AfterNg.Stop"/>.</summary>
    Skipped = 4,
}

/// <summary>Result of one step.</summary>
public sealed class StepResult
{
    internal StepResult(string name) => Name = name;

    /// <summary>The step name.</summary>
    public string Name { get; }

    /// <summary>The state.</summary>
    public StepState State { get; internal set; } = StepState.Skipped;   // 돌기 전에는 '안 돌았다'

    /// <summary>Why it is NG, Error or Skipped; <see langword="null"/> when OK.</summary>
    /// <remarks>
    /// NG: the NG codes, joined with ", ". Error: the exception message, the <c>ctx.Fail</c> reason, "Timeout (limit … ms, took … ms)" or "Canceled".
    /// Skipped: "Disabled", "Pose '…' missing (…)", "Stopped after NG in …", "Skipped after timeout in …", "Canceled",
    /// and for reason steps "Not needed (judged OK)" or "Not run (judgment is Error)".
    /// </remarks>
    public string? Reason { get; internal set; }

    /// <summary>Time the step took.</summary>
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>The pose the step's ROI followed, or <see langword="null"/>.</summary>
    public string? Followed { get; internal set; }

    /// <summary>The last image the step created (for tuning in the result view, <c>DvVisionView</c> of Devinno.Vision.UI), or <see langword="null"/>. Owned by the result: released when the result is disposed.</summary>
    public VisionImage? Image { get; internal set; }
}

/// <summary>Step results in the order the steps were added, also looked up by name: <c>r.Steps["Holes"]</c>.</summary>
/// <remarks>Every step of the job has an entry, also the ones that did not run (<see cref="StepState.Skipped"/>).</remarks>
public sealed class StepResultList : IReadOnlyList<StepResult>
{
    private readonly List<StepResult> _items = [];

    internal void Add(StepResult step) => _items.Add(step);

    /// <summary>The step result with the given name.</summary>
    /// <param name="name">The step name.</param>
    /// <exception cref="KeyNotFoundException">No step has that name.</exception>
    public StepResult this[string name] => _items.Find(s => s.Name == name) ?? throw new KeyNotFoundException($"No step named '{name}'.");

    /// <inheritdoc/>
    public StepResult this[int index] => _items[index];

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public IEnumerator<StepResult> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One measured value with its limits.</summary>
/// <param name="Name">Value name.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Min">Lower limit, or <see langword="null"/>.</param>
/// <param name="Max">Upper limit, or <see langword="null"/>.</param>
/// <param name="Unit">Unit text, or <see langword="null"/>.</param>
/// <param name="Code">NG code used when out of range.</param>
/// <param name="Ok">Whether the value is within the limits.</param>
/// <param name="Step">The step that recorded it.</param>
/// <param name="Nominal">The nominal value when recorded with tolerances, otherwise <see langword="null"/>.</param>
public sealed record VisionValue(string Name, double Value, double? Min, double? Max, string? Unit, string Code, bool Ok, string Step, double? Nominal = null)
{
    /// <summary>Value − nominal, or <see langword="null"/> without a nominal.</summary>
    public double? Deviation => Nominal is { } n ? Value - n : null;
}

/// <summary>Kind of an overlay shape.</summary>
public enum VisionOverlayKind
{
    /// <summary>A step's ROI border (4 corners).</summary>
    Roi,

    /// <summary>A box (4 corners).</summary>
    Box,

    /// <summary>A closed outline.</summary>
    Contour,

    /// <summary>A line (2 points).</summary>
    Line,

    /// <summary>A point (1 point).</summary>
    Point,

    /// <summary>Text at a point (1 point + <see cref="VisionOverlay.Label"/>).</summary>
    Text,
}

/// <summary>A shape to draw over the image, in the original image's coordinates.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Points">The points (original image, px).</param>
/// <param name="Label">Text shown next to the shape, or <see langword="null"/>.</param>
/// <param name="Step">The step that drew it.</param>
public sealed record VisionOverlay(VisionOverlayKind Kind, IReadOnlyList<Point2d> Points, string? Label, string Step);
