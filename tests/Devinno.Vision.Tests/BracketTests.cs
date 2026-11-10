using Devinno.Vision;
using Xunit.Abstractions;

// 1단계 완료 기준: Bracket 샘플 18장을 파일 이름대로 판정한다 (베이스 = 01_good 에서 기준 자세를 티칭)
public class BracketTests(ITestOutputHelper output)
{
    [Fact]
    public async Task All_samples_are_judged_as_their_file_names_say()
    {
        var setup = BracketInspection.DefaultSetup();
        var job = BracketInspection.CreateJob();
        job.Setup = setup;

        // 티칭: 베이스 이미지에서 기준 자세를 얻어 설정 복사본에 넣고 통째로 교체
        using (var baseImg = VisionImage.Load(BracketInspection.Sample("01_good.bmp")))
        using (var r0 = await job.RunAsync(baseImg))
        {
            Assert.Equal(VisionState.Ok, r0.State);
            var edit = VisionSetup.Copy(setup);
            edit.BodyRef = r0.Poses["본체"];
            job.Setup = setup = edit;
        }

        var failures = new List<string>();
        foreach (var f in Directory.GetFiles(BracketInspection.SampleDir, "*.bmp").Order())
        {
            var name = Path.GetFileNameWithoutExtension(f);
            using var img = VisionImage.Load(f);
            using var r = await job.RunAsync(img);
            var expected = name.Split('_')[1] switch
            {
                "good" => (VisionState.Ok, (string?)null),
                "missinghole" => (VisionState.Ng, "HOLE"),
                "tabdefect" => (VisionState.Ng, "DISCOLOR"),
                "distancedefect" => (VisionState.Ng, "LENGTH"),
                var other => throw new InvalidOperationException(other),
            };
            var codes = string.Join(",", r.Codes);
            output.WriteLine($"{name,-20} {r.State,-5} {codes,-10} {r.Elapsed.TotalMilliseconds,6:0.0} ms  {string.Join(" ", r.Values.Select(v => $"{v.Name}={v.Value:0.##}"))}");
            if (r.State != expected.Item1 || (expected.Item2 is { } code && !(r.Codes.Count == 1 && r.Codes[0] == code)))
                failures.Add($"{name}: expected {expected.Item1} {expected.Item2}, got {r.State} {codes} {r.Error}");
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Rotated_part_ROIs_follow_the_part()
    {
        var setup = BracketInspection.DefaultSetup();
        var job = BracketInspection.CreateJob();
        job.Setup = setup;
        using (var b = VisionImage.Load(BracketInspection.Sample("01_good.bmp")))
        using (var r0 = await job.RunAsync(b))
            setup.BodyRef = r0.Poses["본체"];

        using var img = VisionImage.Load(BracketInspection.Sample("18_good.bmp"));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ok, r.State);
        Assert.InRange(r.Poses["본체"].Angle, -22, -18);
        Assert.Equal("본체", r.Steps["상단 변색"].Followed);
        var roi = r.Overlays.First(o => o.Kind == VisionOverlayKind.Roi && o.Step == "상단 변색");
        // 회전된 테두리: 윗변이 기울어 있다
        var dy = roi.Points[1].Y - roi.Points[0].Y;
        Assert.True(dy < -20, $"ROI top edge should tilt up to the right, dy = {dy:0.0}");
    }
}
