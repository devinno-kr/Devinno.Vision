namespace Devinno.Vision;

/// <summary>
/// A step as an object, for steps that are not lambdas (reusable classes, and later the teaching tool's standard and script steps).
/// It runs exactly like a lambda step: same context, same result format.
/// </summary>
public interface IVisionStep
{
    /// <summary>The step name (unique in the job; shown in results and on screen).</summary>
    string Name { get; }

    /// <summary>Runs the step. Record values and judgments through <paramref name="ctx"/>.</summary>
    /// <param name="ctx">The step context.</param>
    /// <returns>A task that completes when the step is done.</returns>
    Task RunAsync(VisionContext ctx);
}

/// <summary>What a job does with the remaining steps after a step is NG.</summary>
public enum AfterNg
{
    /// <summary>Keep running (default): all values are recorded and every defect is shown.</summary>
    Continue,

    /// <summary>Stop: the remaining steps are Skipped. For jobs with a tight cycle time.</summary>
    Stop,
}

/// <summary>What a step's result means for the job's judgment.</summary>
public enum StepRole
{
    /// <summary>The step judges (default): its NG makes the result NG, its Error makes it Error.</summary>
    Judge,

    /// <summary>
    /// The step only says why a part is NG. It runs after the judging steps (see <see cref="ReasonRun"/>); its NG codes are added to an NG result,
    /// and it never turns an OK result into NG or an NG result into Error. Used when a model judges and standard checks name the defect.
    /// A step that makes a pose cannot be a reason step.
    /// </summary>
    Reason,
}

/// <summary>When reason steps (<see cref="StepRole.Reason"/>) run.</summary>
public enum ReasonRun
{
    /// <summary>Only when the judging steps made the result NG (default): good parts do not pay for them.</summary>
    WhenNg,

    /// <summary>Always, to show their values (for example on a tuning screen). They still do not change the judgment, and their codes are added only to an NG result.</summary>
    Always,
}

// 람다 단계 — 설정 · 과정 정보는 ctx 의 내부 값으로 받는다
internal sealed class LambdaStep(string name, Func<VisionContext, Task> run) : IVisionStep
{
    public string Name { get; } = name;

    public Task RunAsync(VisionContext ctx) => run(ctx);
}

// 단계 + Job 에 등록할 때 붙인 선언
internal sealed record StepEntry(IVisionStep Step, Func<object?, VisionRoi>? Roi, string? Pose, string? Follow, TimeSpan? Timeout, StepRole Role = StepRole.Judge);
