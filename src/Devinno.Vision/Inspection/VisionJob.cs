namespace Devinno.Vision;

/// <summary>
/// One inspection of one image: a list of steps run in order. Setup class and process data class are the app's.
/// </summary>
/// <typeparam name="TSetup">The app's setup class (ROIs, reference images, parameters, limits).</typeparam>
/// <typeparam name="TData">The app's per-part data class, shared by the jobs inspecting the same part.</typeparam>
/// <remarks>
/// <para>One image → one job → one result. Each step is one check item with its own ROI; five check items on one image are five steps in one job.</para>
/// <para><b>Alignment</b>: a step declared with <c>pose:</c> finds the part and calls <c>ctx.SetPose</c>; the ROIs of the following steps then follow the part
/// (moved and rotated by the difference from the base image). With one alignment there is nothing more to write; with several, choose with <c>follow:</c>.
/// <c>follow: ""</c> keeps a ROI fixed.</para>
/// <para><b>Errors</b>: <see cref="RunAsync"/> never throws for inspection problems — exceptions, timeouts and missing poses end up in the result.</para>
/// </remarks>
public sealed class VisionJob<TSetup, TData> where TSetup : class
{
    private readonly JobCore _core;

    /// <summary>Creates a job.</summary>
    /// <param name="name">The job name (for example the face being inspected).</param>
    public VisionJob(string name) => _core = new JobCore(name);

    /// <summary>The job name.</summary>
    public string Name => _core.Name;

    /// <summary>The setup. A run uses the reference it took when it started; to change values while running, edit a copy (<see cref="VisionSetup.Copy{T}"/>) and replace it.</summary>
    public TSetup? Setup { get; set; }

    /// <inheritdoc cref="VisionJob.AfterNg"/>
    public AfterNg AfterNg { get => _core.AfterNg; set => _core.AfterNg = value; }

    /// <inheritdoc cref="VisionJob.Timeout"/>
    public TimeSpan? Timeout { get => _core.Timeout; set => _core.Timeout = value; }

    /// <inheritdoc cref="VisionJob.Calibration"/>
    public VisionCalibration? Calibration { get => _core.Calibration; set => _core.Calibration = value; }

    /// <summary>Adds a step.</summary>
    /// <param name="name">Step name (unique in the job).</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step; <see langword="null"/> = <see cref="Timeout"/>.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup, TData> Add(string name, Action<VisionContext, TSetup, TData> step, TimeSpan? timeout = null)
        => Add(name, null, null, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Action{VisionContext, TSetup, TData}, TimeSpan?)"/>
    public VisionJob<TSetup, TData> Add(string name, Func<VisionContext, TSetup, TData, Task> step, TimeSpan? timeout = null)
        => Add(name, null, null, null, step, timeout);

    /// <summary>Adds an alignment step: it finds the part and calls <c>ctx.SetPose(current, reference)</c>; the ROIs of later steps follow the pose.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="pose">The pose name this step makes.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup, TData> Add(string name, string pose, Action<VisionContext, TSetup, TData> step, TimeSpan? timeout = null)
        => Add(name, null, pose, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, string, Action{VisionContext, TSetup, TData}, TimeSpan?)"/>
    public VisionJob<TSetup, TData> Add(string name, string pose, Func<VisionContext, TSetup, TData, Task> step, TimeSpan? timeout = null)
        => Add(name, null, pose, null, step, timeout);

    /// <summary>Adds a step with a ROI. Inside the step, <c>ctx.Image</c> is the ROI, upright and following the part's pose.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="roi">Which setup property is the ROI (base image coordinates).</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup, TData> Add(string name, Func<TSetup, VisionRoi> roi, Action<VisionContext, TSetup, TData> step, TimeSpan? timeout = null)
        => Add(name, roi, null, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Func{TSetup, VisionRoi}, Action{VisionContext, TSetup, TData}, TimeSpan?)"/>
    public VisionJob<TSetup, TData> Add(string name, Func<TSetup, VisionRoi> roi, Func<VisionContext, TSetup, TData, Task> step, TimeSpan? timeout = null)
        => Add(name, roi, null, null, step, timeout);

    /// <summary>Adds a step with a ROI that follows a named pose.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="roi">Which setup property is the ROI.</param>
    /// <param name="follow">The pose to follow; <c>""</c> = do not follow (fixed ROI).</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup, TData> Add(string name, Func<TSetup, VisionRoi> roi, string follow, Action<VisionContext, TSetup, TData> step, TimeSpan? timeout = null)
        => Add(name, roi, null, follow, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Func{TSetup, VisionRoi}, string, Action{VisionContext, TSetup, TData}, TimeSpan?)"/>
    public VisionJob<TSetup, TData> Add(string name, Func<TSetup, VisionRoi> roi, string follow, Func<VisionContext, TSetup, TData, Task> step, TimeSpan? timeout = null)
        => Add(name, roi, null, follow, step, timeout);

    /// <summary>Adds a step object.</summary>
    /// <param name="step">The step.</param>
    /// <param name="roi">Which setup property is the ROI, or <see langword="null"/> for the whole image.</param>
    /// <param name="pose">The pose this step makes, or <see langword="null"/>.</param>
    /// <param name="follow">The pose to follow (<see langword="null"/> = automatic, <c>""</c> = none).</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup, TData> Add(IVisionStep step, Func<TSetup, VisionRoi>? roi = null, string? pose = null, string? follow = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        _core.Add(new StepEntry(step, roi is null ? null : s => roi((TSetup)s!), pose, follow, timeout));
        return this;
    }

    /// <inheritdoc cref="VisionJob.Enable"/>
    public VisionJob<TSetup, TData> Enable(string step) { _core.SetEnabled(step, true); return this; }

    /// <inheritdoc cref="VisionJob.Disable"/>
    public VisionJob<TSetup, TData> Disable(string step) { _core.SetEnabled(step, false); return this; }

    /// <summary>Runs the job on an image. Never throws for inspection problems; returns Error (<c>"Busy"</c>) at once if this job is already running.</summary>
    /// <param name="image">The image (stays yours; the result borrows it).</param>
    /// <param name="data">The part's data object — required: jobs inspecting the same part share one object; a new part needs a new object.</param>
    /// <param name="ct">Cancels the run (remaining steps are skipped; the result is Error).</param>
    /// <returns>The result. Returns only after every step has really finished, even after a timeout.</returns>
    public Task<VisionResult> RunAsync(VisionImage image, TData data, CancellationToken ct = default)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data), "Pass the part's data object. Jobs inspecting the same part share one object.");
        return _core.RunAsync(image, Setup, data, needsSetup: true, ct);
    }

    private VisionJob<TSetup, TData> Add(string name, Func<TSetup, VisionRoi>? roi, string? pose, string? follow, Func<VisionContext, TSetup, TData, Task> step, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Add(new LambdaStep(name, ctx => step(ctx, (TSetup)ctx.Setup!, (TData)ctx.Data!)), roi, pose, follow, timeout);
    }

    private static Func<VisionContext, TSetup, TData, Task> Wrap(Action<VisionContext, TSetup, TData> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return (c, s, d) => { step(c, s, d); return Task.CompletedTask; };
    }
}

/// <summary>A job with a setup class and no per-part data. See <see cref="VisionJob{TSetup, TData}"/>.</summary>
/// <typeparam name="TSetup">The app's setup class.</typeparam>
public sealed class VisionJob<TSetup> where TSetup : class
{
    private readonly JobCore _core;

    /// <summary>Creates a job.</summary>
    /// <param name="name">The job name.</param>
    public VisionJob(string name) => _core = new JobCore(name);

    /// <summary>The job name.</summary>
    public string Name => _core.Name;

    /// <inheritdoc cref="VisionJob{TSetup, TData}.Setup"/>
    public TSetup? Setup { get; set; }

    /// <inheritdoc cref="VisionJob.AfterNg"/>
    public AfterNg AfterNg { get => _core.AfterNg; set => _core.AfterNg = value; }

    /// <inheritdoc cref="VisionJob.Timeout"/>
    public TimeSpan? Timeout { get => _core.Timeout; set => _core.Timeout = value; }

    /// <inheritdoc cref="VisionJob.Calibration"/>
    public VisionCalibration? Calibration { get => _core.Calibration; set => _core.Calibration = value; }

    /// <summary>Adds a step.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup> Add(string name, Action<VisionContext, TSetup> step, TimeSpan? timeout = null)
        => Add(name, null, null, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Action{VisionContext, TSetup}, TimeSpan?)"/>
    public VisionJob<TSetup> Add(string name, Func<VisionContext, TSetup, Task> step, TimeSpan? timeout = null)
        => Add(name, null, null, null, step, timeout);

    /// <summary>Adds an alignment step (see <see cref="VisionJob{TSetup, TData}"/>).</summary>
    /// <param name="name">Step name.</param>
    /// <param name="pose">The pose name this step makes.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup> Add(string name, string pose, Action<VisionContext, TSetup> step, TimeSpan? timeout = null)
        => Add(name, null, pose, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, string, Action{VisionContext, TSetup}, TimeSpan?)"/>
    public VisionJob<TSetup> Add(string name, string pose, Func<VisionContext, TSetup, Task> step, TimeSpan? timeout = null)
        => Add(name, null, pose, null, step, timeout);

    /// <summary>Adds a step with a ROI (following the part's pose).</summary>
    /// <param name="name">Step name.</param>
    /// <param name="roi">Which setup property is the ROI.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup> Add(string name, Func<TSetup, VisionRoi> roi, Action<VisionContext, TSetup> step, TimeSpan? timeout = null)
        => Add(name, roi, null, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Func{TSetup, VisionRoi}, Action{VisionContext, TSetup}, TimeSpan?)"/>
    public VisionJob<TSetup> Add(string name, Func<TSetup, VisionRoi> roi, Func<VisionContext, TSetup, Task> step, TimeSpan? timeout = null)
        => Add(name, roi, null, null, step, timeout);

    /// <summary>Adds a step with a ROI that follows a named pose (<c>""</c> = fixed).</summary>
    /// <param name="name">Step name.</param>
    /// <param name="roi">Which setup property is the ROI.</param>
    /// <param name="follow">The pose to follow; <c>""</c> = do not follow.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob<TSetup> Add(string name, Func<TSetup, VisionRoi> roi, string follow, Action<VisionContext, TSetup> step, TimeSpan? timeout = null)
        => Add(name, roi, null, follow, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Func{TSetup, VisionRoi}, string, Action{VisionContext, TSetup}, TimeSpan?)"/>
    public VisionJob<TSetup> Add(string name, Func<TSetup, VisionRoi> roi, string follow, Func<VisionContext, TSetup, Task> step, TimeSpan? timeout = null)
        => Add(name, roi, null, follow, step, timeout);

    /// <inheritdoc cref="VisionJob{TSetup, TData}.Add(IVisionStep, Func{TSetup, VisionRoi}?, string?, string?, TimeSpan?)"/>
    public VisionJob<TSetup> Add(IVisionStep step, Func<TSetup, VisionRoi>? roi = null, string? pose = null, string? follow = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        _core.Add(new StepEntry(step, roi is null ? null : s => roi((TSetup)s!), pose, follow, timeout));
        return this;
    }

    /// <inheritdoc cref="VisionJob.Enable"/>
    public VisionJob<TSetup> Enable(string step) { _core.SetEnabled(step, true); return this; }

    /// <inheritdoc cref="VisionJob.Disable"/>
    public VisionJob<TSetup> Disable(string step) { _core.SetEnabled(step, false); return this; }

    /// <summary>Runs the job on an image. Never throws for inspection problems.</summary>
    /// <param name="image">The image (stays yours; the result borrows it).</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The result, after every step has really finished.</returns>
    public Task<VisionResult> RunAsync(VisionImage image, CancellationToken ct = default)
        => _core.RunAsync(image, Setup, null, needsSetup: true, ct);

    private VisionJob<TSetup> Add(string name, Func<TSetup, VisionRoi>? roi, string? pose, string? follow, Func<VisionContext, TSetup, Task> step, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Add(new LambdaStep(name, ctx => step(ctx, (TSetup)ctx.Setup!)), roi, pose, follow, timeout);
    }

    private static Func<VisionContext, TSetup, Task> Wrap(Action<VisionContext, TSetup> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return (c, s) => { step(c, s); return Task.CompletedTask; };
    }
}

/// <summary>A job without setup or per-part data — for quick inspections and tests. See <see cref="VisionJob{TSetup, TData}"/>.</summary>
public sealed class VisionJob
{
    private readonly JobCore _core;

    /// <summary>Creates a job.</summary>
    /// <param name="name">The job name.</param>
    public VisionJob(string name) => _core = new JobCore(name);

    /// <summary>The job name.</summary>
    public string Name => _core.Name;

    /// <summary>What happens to the remaining steps after a step is NG. Default <see cref="Vision.AfterNg.Continue"/>.</summary>
    public AfterNg AfterNg { get => _core.AfterNg; set => _core.AfterNg = value; }

    /// <summary>Default time limit of each step; <see langword="null"/> (default) = none. When exceeded, <c>ctx.Cancel</c> is signaled, the run waits for the step
    /// to really end (OpenCV calls cannot be stopped), skips the remaining steps and ends in Error.</summary>
    public TimeSpan? Timeout { get => _core.Timeout; set => _core.Timeout = value; }

    /// <summary>Pixel → mm conversion used by <c>ctx.Mm</c> (a machine value: keep it with the camera setup, not with the product setup). <see langword="null"/> = none; <c>ctx.Mm</c> then makes the step Error.</summary>
    public VisionCalibration? Calibration { get => _core.Calibration; set => _core.Calibration = value; }

    /// <summary>Adds a step.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob Add(string name, Action<VisionContext> step, TimeSpan? timeout = null)
        => Add(name, null, null, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Action{VisionContext}, TimeSpan?)"/>
    public VisionJob Add(string name, Func<VisionContext, Task> step, TimeSpan? timeout = null)
        => Add(name, null, null, null, step, timeout);

    /// <summary>Adds an alignment step.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="pose">The pose name this step makes.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob Add(string name, string pose, Action<VisionContext> step, TimeSpan? timeout = null)
        => Add(name, null, pose, null, Wrap(step), timeout);

    /// <summary>Adds a step with a ROI.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="roi">Returns the ROI (base image coordinates).</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob Add(string name, Func<VisionRoi> roi, Action<VisionContext> step, TimeSpan? timeout = null)
        => Add(name, roi, null, null, Wrap(step), timeout);

    /// <inheritdoc cref="Add(string, Func{VisionRoi}, Action{VisionContext}, TimeSpan?)"/>
    public VisionJob Add(string name, Func<VisionRoi> roi, Func<VisionContext, Task> step, TimeSpan? timeout = null)
        => Add(name, roi, null, null, step, timeout);

    /// <summary>Adds a step with a ROI that follows a named pose (<c>""</c> = fixed).</summary>
    /// <param name="name">Step name.</param>
    /// <param name="roi">Returns the ROI.</param>
    /// <param name="follow">The pose to follow; <c>""</c> = do not follow.</param>
    /// <param name="step">The step code.</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob Add(string name, Func<VisionRoi> roi, string follow, Action<VisionContext> step, TimeSpan? timeout = null)
        => Add(name, roi, null, follow, Wrap(step), timeout);

    /// <summary>Adds a step object.</summary>
    /// <param name="step">The step.</param>
    /// <param name="roi">Returns the ROI, or <see langword="null"/> for the whole image.</param>
    /// <param name="pose">The pose this step makes, or <see langword="null"/>.</param>
    /// <param name="follow">The pose to follow (<see langword="null"/> = automatic, <c>""</c> = none).</param>
    /// <param name="timeout">Time limit of this step.</param>
    /// <returns>This job.</returns>
    public VisionJob Add(IVisionStep step, Func<VisionRoi>? roi = null, string? pose = null, string? follow = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        _core.Add(new StepEntry(step, roi is null ? null : _ => roi(), pose, follow, timeout));
        return this;
    }

    /// <summary>Turns a step back on.</summary>
    /// <param name="step">Step name.</param>
    /// <returns>This job.</returns>
    /// <exception cref="ArgumentException">No step has that name.</exception>
    public VisionJob Enable(string step) { _core.SetEnabled(step, true); return this; }

    /// <summary>Turns a step off. It is Skipped, listed in <see cref="VisionResult.Disabled"/> and warned about on screen — a disabled step does not affect the judgment.</summary>
    /// <param name="step">Step name.</param>
    /// <returns>This job.</returns>
    /// <exception cref="ArgumentException">No step has that name.</exception>
    public VisionJob Disable(string step) { _core.SetEnabled(step, false); return this; }

    /// <summary>Runs the job on an image. Never throws for inspection problems.</summary>
    /// <param name="image">The image (stays yours; the result borrows it).</param>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The result, after every step has really finished.</returns>
    public Task<VisionResult> RunAsync(VisionImage image, CancellationToken ct = default)
        => _core.RunAsync(image, null, null, needsSetup: false, ct);

    private VisionJob Add(string name, Func<VisionRoi>? roi, string? pose, string? follow, Func<VisionContext, Task> step, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Add(new LambdaStep(name, step), roi, pose, follow, timeout);
    }

    private static Func<VisionContext, Task> Wrap(Action<VisionContext> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return c => { step(c); return Task.CompletedTask; };
    }
}
