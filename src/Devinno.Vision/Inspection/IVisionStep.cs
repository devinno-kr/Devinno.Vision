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

// 람다 단계 — 설정 · 과정 정보는 ctx 의 내부 값으로 받는다
internal sealed class LambdaStep(string name, Func<VisionContext, Task> run) : IVisionStep
{
    public string Name { get; } = name;

    public Task RunAsync(VisionContext ctx) => run(ctx);
}

// 단계 + Job 에 등록할 때 붙인 선언
internal sealed record StepEntry(IVisionStep Step, Func<object?, VisionRoi>? Roi, string? Pose, string? Follow, TimeSpan? Timeout);
