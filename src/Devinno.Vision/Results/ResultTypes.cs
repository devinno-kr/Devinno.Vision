using System.Collections;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>Judgment of a whole job run.</summary>
public enum VisionState
{
    /// <summary>Inspected and good.</summary>
    Ok,

    /// <summary>Inspected and defective (<see cref="VisionResult.Codes"/> says why).</summary>
    Ng,

    /// <summary>Could not inspect: exception, timeout, alignment missing, busy, … (<see cref="VisionResult.Error"/> says why).</summary>
    Error,
}

/// <summary>State of one step.</summary>
public enum StepState
{
    /// <summary>Ran and good.</summary>
    Ok,

    /// <summary>Ran and defective.</summary>
    Ng,

    /// <summary>Could not inspect (exception, <c>ctx.Fail</c>, timeout, ROI not set, …).</summary>
    Error,

    /// <summary>Did not run: disabled, its pose is missing, after a timeout, or after NG with <see cref="AfterNg.Stop"/>.</summary>
    Skipped,
}

/// <summary>Result of one step.</summary>
public sealed class StepResult
{
    internal StepResult(string name) => Name = name;

    /// <summary>The step name.</summary>
    public string Name { get; }

    /// <summary>The state.</summary>
    public StepState State { get; internal set; }

    /// <summary>Why it is NG, Error or Skipped; <see langword="null"/> when OK.</summary>
    public string? Reason { get; internal set; }

    /// <summary>Time the step took.</summary>
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>The pose the step's ROI followed, or <see langword="null"/>.</summary>
    public string? Followed { get; internal set; }

    /// <summary>The last image the step created (for tuning in the result view, <c>DvVisionView</c> of Devinno.Vision.UI), or <see langword="null"/>. Owned by the result: released when the result is disposed.</summary>
    public VisionImage? Image { get; internal set; }
}

/// <summary>Step results in run order, also looked up by name: <c>r.Steps["Holes"]</c>.</summary>
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
public sealed record VisionValue(string Name, double Value, double? Min, double? Max, string? Unit, string Code, bool Ok, string Step);

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
