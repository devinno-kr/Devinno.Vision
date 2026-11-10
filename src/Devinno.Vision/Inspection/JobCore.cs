using System.Diagnostics;
using OpenCvSharp;

namespace Devinno.Vision;

// 실행 한 번의 상태 (범위 · 결과 · Pose)
internal sealed class JobRun
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (VisionPose Current, VisionPose Reference, string Step)> _poses = [];

    public JobRun(object? setup, object? data, VisionScope scope, VisionImage frame, VisionResult result)
    {
        Setup = setup;
        Data = data;
        Scope = scope;
        Frame = frame;
        Result = result;
    }

    public object? Setup { get; }
    public object? Data { get; }
    public VisionScope Scope { get; }
    public VisionImage Frame { get; }
    public VisionResult Result { get; }
    public string? LastPose { get; private set; }

    public void SetPose(string name, VisionPose current, VisionPose reference, string step)
    {
        lock (_gate)
        {
            _poses[name] = (current, reference, step);
            Result.PoseMap[name] = current;
            LastPose = name;
        }
    }

    public bool TryGetMotion(string name, out Affine motion)
    {
        lock (_gate)
        {
            if (_poses.TryGetValue(name, out var p))
            {
                motion = VisionPose.Motion(p.Reference, p.Current);
                return true;
            }
        }
        motion = Affine.Identity;
        return false;
    }

    public bool HasAnyPose { get { lock (_gate) return _poses.Count > 0; } }

    // 기준 좌표의 ROI 를 지금 부품 자리로 옮겨 자른다
    public VisionImage Place(VisionRoi roi, Affine? motion)
    {
        if (roi.IsEmpty)
            throw new ArgumentException("The ROI is not set (empty or zero size).");
        var center = roi.Center;
        var angle = roi.PlacedAngle;
        if (motion is { } m && !m.IsIdentity)
        {
            center = m.Map(center);
            angle = VisionPose.Normalize(angle + Math.Atan2(m.D, m.A) * 180 / Math.PI);
        }
        return Frame.CropPlaced(center, roi.Width, roi.Height, angle);
    }

    public void AddRoiOverlay(VisionRoi roi, Affine? motion, string step, string? followed)
    {
        if (roi.IsEmpty)
            return;
        var corners = roi.Corners();
        if (motion is { } m)
            corners = corners.Select(m.Map).ToArray();
        AddOverlay(new VisionOverlay(VisionOverlayKind.Roi, corners, followed is null ? step : $"{step} ← {followed}", step));
    }

    public void AddValue(VisionValue v)
    {
        lock (_gate)
            Result.ValueList.Add(v);
    }

    public void AddOverlay(VisionOverlay o)
    {
        lock (_gate)
            Result.OverlayList.Add(o);
    }
}

// Job 세 종류(VisionJob · <TSetup> · <TSetup, TData>)가 공유하는 실행기
internal sealed class JobCore(string name)
{
    private readonly object _gate = new();
    private readonly List<StepEntry> _steps = [];
    private readonly HashSet<string> _disabled = [];
    private int _busy;

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public AfterNg AfterNg { get; set; } = AfterNg.Continue;

    public TimeSpan? Timeout { get; set; }

    public void Add(StepEntry entry)
    {
        var stepName = entry.Step.Name;
        if (string.IsNullOrWhiteSpace(stepName))
            throw new ArgumentException("A step needs a name.");
        lock (_gate)
        {
            if (_steps.Any(s => s.Step.Name == stepName))
                throw new ArgumentException($"The job '{Name}' already has a step named '{stepName}'.");
            // follow 오타를 등록할 때 잡는다: 앞 단계들이 Pose 를 선언했는데 그 이름이 없으면
            if (!string.IsNullOrEmpty(entry.Follow))
            {
                var declared = _steps.Where(s => s.Pose is not null).Select(s => s.Pose!).Distinct().ToList();
                if (declared.Count > 0 && !declared.Contains(entry.Follow))
                    throw new ArgumentException($"{stepName}: no earlier step makes the pose '{entry.Follow}'. Declared poses: {string.Join(", ", declared)}.");
            }
            _steps.Add(entry);
        }
    }

    public void SetEnabled(string step, bool enabled)
    {
        lock (_gate)
        {
            if (!_steps.Any(s => s.Step.Name == step))
                throw new ArgumentException($"The job '{Name}' has no step named '{step}'.", nameof(step));
            if (enabled) _disabled.Remove(step);
            else _disabled.Add(step);
        }
    }

    public async Task<VisionResult> RunAsync(VisionImage image, object? setup, object? data, bool needsSetup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return new VisionResult(Name, image) { State = VisionState.Error, Error = "Busy" };
        try
        {
            // 호출 스레드(UI 등)를 막지 않게 — 동기 단계도 여기서 돈다
            return await Task.Run(() => RunCore(image, setup, data, needsSetup, ct)).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task<VisionResult> RunCore(VisionImage image, object? setup, object? data, bool needsSetup, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var result = new VisionResult(Name, image);
        StepEntry[] steps;
        HashSet<string> disabled;
        AfterNg afterNg;
        TimeSpan? jobTimeout;
        lock (_gate)
        {
            steps = [.. _steps];
            disabled = [.. _disabled];
            afterNg = AfterNg;
            jobTimeout = Timeout;
        }

        var scope = new VisionScope(Name);
        try
        {
            if (needsSetup && setup is null)
                return Finish(result, VisionState.Error, $"{Name}: Setup is not set.", total);
            if (steps.Length == 0)
                return Finish(result, VisionState.Error, $"{Name}: the job has no steps.", total);

            VisionImage frame;
            try
            {
                frame = new VisionImage(VisionImage.Share(image.Mat), image.Info, Affine.Identity, scope);
            }
            catch (Exception ex)
            {
                return Finish(result, VisionState.Error, $"{Name}: {ex.Message}", total);
            }

            var run = new JobRun(setup, data, scope, frame, result);
            string? stopReason = null;     // 남은 단계를 건너뛰는 이유
            var stopIsError = false;
            string? firstError = null;
            var anyNg = false;
            var anyRan = false;
            string? poseMissing = null;    // Pose 가 없어 건너뛴 단계 (다른 판정이 없으면 Error)
            var declaredBefore = new HashSet<string>();
            var poseSteps = new Dictionary<string, string>();

            foreach (var entry in steps)
            {
                var name = entry.Step.Name;
                var sr = new StepResult(name);
                result.Steps.Add(sr);

                if (disabled.Contains(name))
                {
                    sr.State = StepState.Skipped;
                    sr.Reason = "Disabled";
                    result.DisabledList.Add(name);
                    Declare(entry);
                    continue;
                }
                if (stopReason is not null)
                {
                    sr.State = StepState.Skipped;
                    sr.Reason = stopReason;
                    Declare(entry);
                    continue;
                }

                // ── 따라갈 Pose 정하기 ──
                Affine? motion = null;
                string? followed = null;
                var follow = entry.Follow;
                if (follow is null && entry.Pose is null)
                {
                    if (run.LastPose is { } last) follow = last;
                    else if (declaredBefore.Count > 0) follow = declaredBefore.Last();
                }
                if (!string.IsNullOrEmpty(follow) && entry.Pose != follow)
                {
                    if (run.TryGetMotion(follow, out var m))
                    {
                        motion = m;
                        followed = follow;
                    }
                    else
                    {
                        sr.State = StepState.Skipped;
                        sr.Followed = follow;
                        sr.Reason = poseSteps.TryGetValue(follow, out var src) && result.Steps[src] is var ps
                            ? $"Pose '{follow}' missing ({src}: {ps.State}{(ps.Reason is null ? "" : " " + ps.Reason)})"
                            : $"Pose '{follow}' missing";
                        poseMissing ??= $"{Name}/{name}: {sr.Reason}";
                        Declare(entry);
                        continue;
                    }
                }
                sr.Followed = followed;

                // ── 실행 ──
                anyRan = true;
                scope.CurrentStep = name;
                var limit = entry.Timeout ?? jobTimeout;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (limit is { } lim)
                    cts.CancelAfter(lim);
                var sw = Stopwatch.StartNew();
                var timedOut = false;
                var canceled = false;
                VisionContext? ctx = null;
                try
                {
                    VisionImage stepImage;
                    if (entry.Roi is { } roiOf)
                    {
                        var roi = roiOf(setup);
                        if (roi.IsEmpty)
                            throw new InvalidOperationException("The ROI is not set.");
                        stepImage = run.Place(roi, motion);
                        run.AddRoiOverlay(roi, motion, name, followed);
                    }
                    else
                    {
                        stepImage = frame.Derive(VisionImage.Share(frame.Mat), Affine.Identity);
                    }
                    ctx = new VisionContext(run, entry, stepImage, motion, followed, cts.Token);
                    await entry.Step.RunAsync(ctx).ConfigureAwait(false);
                    sw.Stop();
                    if (limit is { } l && sw.Elapsed > l)
                        timedOut = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    sw.Stop();
                    canceled = true;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    sw.Stop();
                    timedOut = true;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    sr.State = StepState.Error;
                    sr.Reason = ex.Message;
                }
                sr.Elapsed = sw.Elapsed;
                scope.CurrentStep = null;

                if (timedOut)
                {
                    sr.State = StepState.Error;
                    sr.Reason = $"Timeout (limit {limit!.Value.TotalMilliseconds:0} ms, took {sw.Elapsed.TotalMilliseconds:0} ms)";
                    stopReason = $"Skipped after timeout in {name}";
                    stopIsError = true;
                }
                else if (canceled)
                {
                    sr.State = StepState.Error;
                    sr.Reason = "Canceled";
                    stopReason = "Canceled";
                    stopIsError = true;
                }
                else if (sr.State != StepState.Error && ctx is not null)
                {
                    if (ctx.FailReason is { } fail)
                    {
                        sr.State = StepState.Error;
                        sr.Reason = fail;
                    }
                    else if (ctx.NgCodes is { Count: > 0 } codes)
                    {
                        sr.State = StepState.Ng;
                        sr.Reason = string.Join(", ", codes);
                        foreach (var c in codes)
                            if (!result.CodeList.Contains(c))
                                result.CodeList.Add(c);
                    }
                    else
                    {
                        sr.State = StepState.Ok;
                    }
                }

                if (sr.State == StepState.Error)
                    firstError ??= $"{Name}/{name}: {sr.Reason}";
                if (sr.State == StepState.Ng)
                {
                    anyNg = true;
                    if (afterNg == AfterNg.Stop && stopReason is null)
                        stopReason = $"Stopped after NG in {name}";
                }

                sr.Image = scope.TakeLast(name);
                Declare(entry);
            }

            void Declare(StepEntry e)
            {
                if (e.Pose is { } p)
                {
                    declaredBefore.Remove(p);
                    declaredBefore.Add(p);  // 마지막 선언이 뒤로
                    poseSteps[p] = e.Step.Name;
                }
            }

            if (firstError is not null || stopIsError)
                return Finish(result, VisionState.Error, firstError ?? $"{Name}: {stopReason}", total);
            if (anyNg)
                return Finish(result, VisionState.Ng, null, total);
            if (poseMissing is not null)
                return Finish(result, VisionState.Error, poseMissing, total);   // 검사를 안 했는데 OK 가 나가지 않게
            if (!anyRan)
                return Finish(result, VisionState.Error, $"{Name}: no step ran (all disabled).", total);
            return Finish(result, VisionState.Ok, null, total);
        }
        catch (Exception ex)
        {
            return Finish(result, VisionState.Error, $"{Name}: {ex.Message}", total);
        }
        finally
        {
            scope.Close();
        }
    }

    private static VisionResult Finish(VisionResult r, VisionState state, string? error, Stopwatch total)
    {
        r.State = state;
        r.Error = error;
        r.Elapsed = total.Elapsed;
        return r;
    }
}
