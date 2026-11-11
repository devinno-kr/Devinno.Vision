using Devinno.Vision;
using OpenCvSharp;

// 판정 단계 / 사유 단계 — 사유 단계는 판정을 바꾸지 않는다
public class StepRoleTests
{
    private sealed class Step(string name, Action<VisionContext> run) : IVisionStep
    {
        public string Name { get; } = name;
        public int Runs { get; private set; }

        public Task RunAsync(VisionContext ctx)
        {
            Runs++;
            run(ctx);
            return Task.CompletedTask;
        }
    }

    private static Step Ok(string name) => new(name, ctx => ctx.Value(name, 1, max: 2, code: name.ToUpperInvariant()));
    private static Step Ng(string name) => new(name, ctx => ctx.Value(name, 5, max: 2, code: name.ToUpperInvariant()));

    private static VisionImage Gray() => VisionImage.From(new Mat(40, 60, MatType.CV_8UC1, new Scalar(128)));

    [Fact]
    public async Task Reason_steps_are_not_run_when_the_part_is_ok()
    {
        var hole = Ng("hole");
        var job = new VisionJob("j").Add(hole, role: StepRole.Reason).Add(Ok("model"));
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Ok, r.State);
        Assert.Empty(r.Codes);
        Assert.Equal(0, hole.Runs);
        Assert.Equal(StepState.Skipped, r.Steps["hole"].State);
        Assert.Equal("Not needed (judged OK)", r.Steps["hole"].Reason);
        Assert.Equal(["hole", "model"], r.Steps.Select(s => s.Name));   // 정의 순서 그대로
    }

    [Fact]
    public async Task Reason_steps_add_their_codes_to_an_ng_result()
    {
        var job = new VisionJob("j")
            .Add(Ng("hole"), role: StepRole.Reason)
            .Add(Ok("length"), role: StepRole.Reason)
            .Add(Ng("model"));
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Ng, r.State);
        Assert.Equal(["MODEL", "HOLE"], r.Codes);
        Assert.Equal(StepState.Ng, r.Steps["hole"].State);
        Assert.Equal(StepState.Ok, r.Steps["length"].State);
        Assert.Equal(3, r.Values.Count);
    }

    [Fact]
    public async Task Always_runs_reason_steps_but_they_never_turn_ok_into_ng()
    {
        var job = new VisionJob("j") { ReasonSteps = ReasonRun.Always }
            .Add(Ok("model"))
            .Add(Ng("hole"), role: StepRole.Reason);
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Ok, r.State);
        Assert.Empty(r.Codes);
        Assert.Equal(StepState.Ng, r.Steps["hole"].State);   // 화면에는 값 · 상태가 보인다
        Assert.Contains(r.Values, v => v.Name == "hole" && !v.Ok);
    }

    [Fact]
    public async Task A_failing_reason_step_does_not_make_the_result_error()
    {
        var job = new VisionJob("j")
            .Add(Ng("model"))
            .Add(new Step("hole", _ => throw new InvalidOperationException("boom")), role: StepRole.Reason);
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Ng, r.State);
        Assert.Null(r.Error);
        Assert.Equal(StepState.Error, r.Steps["hole"].State);
        Assert.Equal("boom", r.Steps["hole"].Reason);
    }

    [Fact]
    public async Task Reason_steps_are_not_run_when_the_judgment_is_error()
    {
        var hole = Ng("hole");
        var job = new VisionJob("j").Add(new Step("model", ctx => ctx.Fail("no part"))).Add(hole, role: StepRole.Reason);
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Error, r.State);
        Assert.Equal(0, hole.Runs);
        Assert.Equal("Not run (judgment is Error)", r.Steps["hole"].Reason);
    }

    [Fact]
    public async Task Stop_after_ng_skips_reason_steps()
    {
        var hole = Ng("hole");
        var job = new VisionJob("j") { AfterNg = AfterNg.Stop }.Add(Ng("model")).Add(hole, role: StepRole.Reason);
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Ng, r.State);
        Assert.Equal(0, hole.Runs);
        Assert.Equal(StepState.Skipped, r.Steps["hole"].State);
    }

    [Fact]
    public async Task Reason_steps_follow_the_pose()
    {
        var align = new Step("align", ctx => ctx.SetPose(new VisionPose(35, 25, 0), new VisionPose(30, 20, 0)));
        var job = new VisionJob("j")
            .Add(align, pose: "part")
            .Add(Ng("model"))
            .Add(Ok("hole"), roi: () => VisionRoi.Rect(5, 5, 10, 10), role: StepRole.Reason);
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal("part", r.Steps["hole"].Followed);
        Assert.Equal(StepState.Ok, r.Steps["hole"].State);
    }

    [Fact]
    public async Task A_job_of_only_reason_steps_is_error()
    {
        var job = new VisionJob("j").Add(Ok("hole"), role: StepRole.Reason);
        using var img = Gray();

        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Error, r.State);
        Assert.Contains("no judging step", r.Error);
    }

    [Fact]
    public void A_step_that_makes_a_pose_cannot_be_a_reason_step()
    {
        var job = new VisionJob("j");

        var ex = Assert.Throws<ArgumentException>(() => job.Add(Ok("align"), pose: "part", role: StepRole.Reason));

        Assert.Contains("must judge", ex.Message);
    }
}
