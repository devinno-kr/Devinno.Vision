using OpenCvSharp;
using SkiaSharp;

namespace Devinno.Vision.UI;

/// <summary>
/// What a vision view shows: a result (image + overlays + judgment) or a plain image, and the ROI being taught.
/// Keep one in the view model and bind it in markup: <c>&lt;VisionView source="{FrontView}"/&gt;</c> (register the tag once with <see cref="Register"/>).
/// </summary>
/// <remarks>
/// <para>The view keeps its own display copy (scaled-down BGRA), so the result and the frame can be disposed right after <see cref="Show(VisionResult)"/>.
/// All members can be called from any thread.</para>
/// <para><b>Teaching</b>: set <see cref="EditRoi"/> while showing the base image; the operator drags the rectangle (move inside, resize at the corners).
/// A new rectangle is drawn only when the ROI is empty — clicking outside an existing ROI does nothing, so a stray click cannot replace it;
/// a click without moving, or a rectangle smaller than 4 px, is ignored. <see cref="RoiChanged"/> reports the new ROI in image coordinates. Rotated ROIs can be moved but not resized yet.</para>
/// </remarks>
public sealed class DvVisionView
{
    /// <summary>The markup tag registered by <see cref="Register"/>. (Devinno.UI reserves tags starting with "Dv" for built-in elements.)</summary>
    public const string Tag = "VisionView";

    private readonly object _gate = new();
    private ViewSnapshot? _snapshot;
    private VisionRoi? _editRoi;
    private long _version;
    private bool _showDisabledWarning = true;
    private string? _step;
    private string _disabledFormat = "Disabled steps ({0}): {1}";

    /// <summary>Registers the <c>VisionView</c> markup tag.</summary>
    /// <param name="registry">The app's component registry (set it as <c>app.Registry</c>).</param>
    public static void Register(Devinno.UI.Components.ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterWidget<VisionViewWidget>(Tag);
    }

    /// <summary>Largest side of the display copy in px (default 1600). Larger images are scaled down when shown.</summary>
    public int MaxDisplaySize { get; set; } = 1600;

    /// <summary>Show a warning when the result has disabled steps (default on). A disabled step does not affect the judgment, so production could pass without checking.</summary>
    public bool ShowDisabledWarning { get => _showDisabledWarning; set { _showDisabledWarning = value; Changed(); } }

    /// <summary>Warning text; {0} = count, {1} = names. Default <c>"Disabled steps ({0}): {1}"</c>.</summary>
    public string DisabledWarningFormat { get => _disabledFormat; set { _disabledFormat = value ?? ""; Changed(); } }

    /// <summary>Show this step's image (<see cref="StepResult.Image"/>, the last image it created — e.g. the binarized one) instead of the inspected image, for tuning on site.
    /// Applies from the next <see cref="Show(VisionResult)"/>. <see langword="null"/> = the inspected image.</summary>
    public string? Step { get => _step; set => _step = value; }

    /// <summary>The ROI being taught (image coordinates), drawn with handles; <see langword="null"/> = not editing.
    /// A ROI with <see cref="VisionRoiShape.None"/> lets the operator draw a new rectangle.</summary>
    public VisionRoi? EditRoi
    {
        get { lock (_gate) return _editRoi; }
        set { lock (_gate) _editRoi = value; Changed(); }
    }

    /// <summary>Raised when the operator finishes dragging the ROI (on the UI thread). The value is in image coordinates.</summary>
    public event Action<VisionRoi>? RoiChanged;

    /// <summary>Shows a result: the image (or the <see cref="Step"/> image), ROI borders colored by step state, overlays, the judgment and warnings.</summary>
    /// <param name="result">The result. It may be disposed right after this call.</param>
    public void Show(VisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stepImage = _step is { } st && result.Steps.FirstOrDefault(s => s.Name == st)?.Image is { IsDisposed: false } si ? si : null;
        var source = stepImage ?? (result.HasImage ? result.Image : null);
        var snap = source is null ? new ViewSnapshot() : ViewSnapshot.FromImage(source.Mat, MaxDisplaySize, stepImage?.ToSourceTransform.Invert());
        snap.State = result.State;
        snap.Error = result.Error;
        snap.Job = result.Job;
        snap.StepName = stepImage is null ? null : _step;
        snap.Overlays = stepImage is null ? [.. result.Overlays] : [.. result.Overlays.Where(o => o.Step == _step)];
        snap.StepStates = result.Steps.ToDictionary(s => s.Name, s => s.State);
        snap.Disabled = [.. result.Disabled];
        snap.Codes = [.. result.Codes];
        Swap(snap);
    }

    /// <summary>Shows an image without a result (for example the base image while teaching, or a grabbed frame).</summary>
    /// <param name="image">The image. It may be disposed right after this call.</param>
    public void Show(VisionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Swap(ViewSnapshot.FromImage(image.Mat, MaxDisplaySize, null));
    }

    /// <summary>Shows a Mat (8-bit gray or BGR) as it is.</summary>
    /// <param name="mat">The Mat. It may be disposed right after this call.</param>
    public void Show(Mat mat)
    {
        ArgumentNullException.ThrowIfNull(mat);
        Swap(ViewSnapshot.FromImage(mat, MaxDisplaySize, null));
    }

    /// <summary>Clears the view.</summary>
    public void Clear() => Swap(null);

    internal long Version => Interlocked.Read(ref _version);

    // 그리는 동안 스냅샷을 잡는다 (교체 · 해제와 겹치지 않게)
    internal void Use(Action<ViewSnapshot?, VisionRoi?, bool, string> use)
    {
        lock (_gate)
            use(_snapshot, _editRoi, _showDisabledWarning, _disabledFormat);
    }

    internal void CommitRoi(VisionRoi roi)
    {
        lock (_gate)
            _editRoi = roi;
        Changed();
        RoiChanged?.Invoke(roi);
    }

    private void Swap(ViewSnapshot? snap)
    {
        ViewSnapshot? old;
        lock (_gate)
        {
            old = _snapshot;
            _snapshot = snap;
            old?.Dispose();
        }
        Changed();
    }

    private void Changed()
    {
        Interlocked.Increment(ref _version);
        // 화면이 쉬고 있으면 깨운다 (어느 스레드에서든 됨). 앱이 아직 없으면 무시
        try { Devinno.UI.App.DvApp.Current.Invalidate(); }
        catch (InvalidOperationException) { }
    }
}
