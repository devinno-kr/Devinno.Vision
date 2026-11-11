using Devinno.Vision;
using OpenCvSharp;

public class CalibrationTests
{
    // 밝은 막대 (x 20 ~ 120, 폭 100 px) — 0.05 mm/px 이면 5.00 mm
    private static VisionImage Bar()
    {
        using var m = new Mat(60, 160, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(m, new Rect(20, 10, 100, 40), Scalar.All(220), -1);
        return VisionImage.From(m);
    }

    [Fact]
    public async Task Caliper_in_mm_with_nominal_and_tolerance()
    {
        using var img = Bar();
        var job = new VisionJob("t") { Calibration = VisionCalibration.FromLength(px: 100, mm: 5) }
            .Add("길이", () => VisionRoi.Rect(5, 20, 150, 20), ctx =>
            {
                var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject);
                ctx.Value("길이", ctx.Mm(e), nominal: 5.0, plus: 0.05, minus: 0.05, unit: "mm", code: "LEN");
            });
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ok, r.State);
        var v = r.Values[0];
        Assert.Equal(5.0, v.Value, 1);
        Assert.Equal(4.95, v.Min!.Value, 6);
        Assert.Equal(5.05, v.Max!.Value, 6);
        Assert.Equal(5.0, v.Nominal);
        Assert.InRange(v.Deviation!.Value, -0.05, 0.05);
        Assert.Contains("\"nominal\":5", r.ToJson());
    }

    [Fact]
    public async Task Out_of_tolerance_is_ng_and_asymmetric_limits_work()
    {
        using var img = Bar();
        var job = new VisionJob("t") { Calibration = VisionCalibration.Scale(0.05) }
            .Add("길이", ctx => ctx.Value("길이", 5.03, nominal: 5.0, plus: 0.02, minus: 0.1, code: "LEN"));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ng, r.State);
        Assert.Equal(4.9, r.Values[0].Min!.Value, 6);
        Assert.Equal(5.02, r.Values[0].Max!.Value, 6);
    }

    [Fact]
    public async Task Mm_follows_rotation_and_resize()
    {
        using var img = Bar();
        double rotated = 0, resized = 0;
        var job = new VisionJob("t") { Calibration = VisionCalibration.Scale(0.05) }
            .Add("회전", () => VisionRoi.Rotated(70, 30, 140, 40, 30), ctx =>
                rotated = ctx.Mm(new Point2d(0, 0), new Point2d(100, 0)))      // 회전 크롭 안의 100 px
            .Add("축소", ctx =>
            {
                var half = ctx.Image.Resize(80, 30);
                resized = ctx.Mm(new Point2d(10, 5), new Point2d(60, 5), from: half);   // 축소 이미지의 50 px = 원본 100 px
            });
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ok, r.State);
        Assert.Equal(5.0, rotated, 6);
        Assert.Equal(5.0, resized, 6);
    }

    [Fact]
    public async Task Missing_calibration_makes_the_step_error()
    {
        using var img = Bar();
        var job = new VisionJob("t").Add("길이", ctx => ctx.Mm(new Point2d(0, 0), new Point2d(1, 0)));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Error, r.State);
        Assert.Contains("calibration", r.Error);
    }

    [Fact]
    public void Calibration_is_plain_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(VisionCalibration.FromLength(400, 20));
        Assert.Equal("{\"MmPerPx\":0.05}", json);
        Assert.Equal(0.05, System.Text.Json.JsonSerializer.Deserialize<VisionCalibration>(json)!.MmPerPx);
        Assert.Throws<ArgumentOutOfRangeException>(() => VisionCalibration.Scale(0));
    }

    // 화면 라벨 단위 = 판정 단위: 캘리브레이션이 있으면 mm, 없으면 px — 단위를 항상 붙인다
    [Theory]
    [InlineData(true, "5.00 mm")]
    [InlineData(false, "100.00 px")]
    public async Task Caliper_label_uses_the_judgment_unit(bool calibrated, string label)
    {
        using var img = Bar();
        var job = new VisionJob("t") { Calibration = calibrated ? VisionCalibration.FromLength(px: 100, mm: 5) : null }
            .Add("길이", () => VisionRoi.Rect(5, 20, 150, 20), ctx => ctx.Draw(Edge.Caliper(ctx.Image, EdgePolarity.BrightObject)));

        using var r = await job.RunAsync(img);

        var line = Assert.Single(r.Overlays, o => o.Kind == VisionOverlayKind.Line);
        Assert.Equal(label, line.Label);
    }
}
