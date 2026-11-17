using Devinno.Vision;
using OpenCvSharp;

public class ImageTests
{
    private static VisionImage Gray(int w = 100, int h = 80, byte value = 50)
    {
        using var m = new Mat(h, w, MatType.CV_8UC1, new Scalar(value));
        return VisionImage.From(m);
    }

    [Fact]
    public void Crop_and_resize_track_coordinates_to_the_source()
    {
        using var img = Gray();
        using var crop = img.Crop(VisionRoi.Rect(10, 20, 40, 30));
        Assert.Equal(new Point2d(15, 25), crop.ToSource(new Point2d(5, 5)));
        using var half = crop.Resize(20, 15);
        var p = half.ToSource(new Point2d(0, 0));
        Assert.Equal(10.5, p.X, 6);   // 화소 중심 규약: 첫 화소 = 원본 두 화소의 가운데
        Assert.Equal(20.5, p.Y, 6);
    }

    [Fact]
    public void Rotated_crop_maps_its_axis_along_the_angle()
    {
        using var img = Gray(200, 200);
        using var crop = img.Crop(VisionRoi.Rotated(100, 100, 40, 20, 90));
        Assert.Equal(40, crop.Width);
        Assert.Equal(20, crop.Height);
        // 90° (시계방향): 크롭의 +x 가 원본의 +y
        var a = crop.ToSource(new Point2d(20, 10));
        var b = crop.ToSource(new Point2d(30, 10));
        Assert.Equal(0, b.X - a.X, 6);
        Assert.Equal(10, b.Y - a.Y, 6);
    }

    [Fact]
    public void Roi_mostly_outside_says_so_partly_outside_is_filled()
    {
        using var img = Gray();   // 100 × 80
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => img.Crop(VisionRoi.Rect(95, 0, 20, 10)));   // 25 % 안
        Assert.Contains("outside the image", ex.Message);
        using var part = img.Crop(VisionRoi.Rect(88, 0, 20, 10));   // 60 % 안 — 바깥은 가장자리 값
        Assert.Equal(20, part.Width);
        Assert.Equal(50, part.Mat.At<byte>(5, 19));
        Assert.Equal(new Point2d(98, 5), part.ToSource(new Point2d(10, 5)));
    }

    [Fact]
    public void Unsupported_type_is_explained()
    {
        using var m = new Mat(10, 10, MatType.CV_16UC1);
        var ex = Assert.Throws<NotSupportedException>(() => VisionImage.From(m));
        Assert.Contains("CV_8UC1", ex.Message);
    }

    [Fact]
    public void Threshold_converts_color_automatically()
    {
        using var m = new Mat(10, 10, MatType.CV_8UC3, new Scalar(200, 200, 200));
        using var img = VisionImage.From(m);
        using var bin = img.Threshold(ThresholdMode.Fixed, 100);
        Assert.Equal(VisionPixelFormat.Gray8, bin.Format);
        Assert.Equal(255, bin.Mat.At<byte>(0, 0));
    }
}

public class JobTests
{
    private static VisionImage Frame()
    {
        using var m = new Mat(100, 100, MatType.CV_8UC1, new Scalar(0));
        Cv2.Rectangle(m, new Rect(30, 30, 40, 20), new Scalar(255), -1);
        return VisionImage.From(m);
    }

    [Fact]
    public async Task Intermediate_images_are_released_and_Keep_survives()
    {
        using var img = Frame();
        VisionImage? leaked = null, kept = null;
        var job = new VisionJob("t").Add("s", ctx =>
        {
            leaked = ctx.Image.Gray().Blur(3);
            kept = ctx.Image.Blur(3).Keep();
        });
        using (var r = await job.RunAsync(img))
            Assert.Equal(VisionState.Ok, r.State);
        var ex = Assert.Throws<ObjectDisposedException>(() => leaked!.Mat);
        Assert.Contains("Keep()", ex.Message);
        Assert.False(kept!.Mat.Empty());
        kept.Dispose();
        Assert.False(img.Mat.Empty());   // frame 은 앱 소유 — 그대로
    }

    [Fact]
    public async Task Exception_in_a_step_is_isolated()
    {
        using var img = Frame();
        var job = new VisionJob("t")
            .Add("a", _ => throw new InvalidOperationException("boom"))
            .Add("b", ctx => ctx.Value("v", 1, min: 0, max: 2));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Error, r.State);
        Assert.Equal("t/a: boom", r.Error);
        Assert.Equal(StepState.Ok, r.Steps["b"].State);
    }

    [Fact]
    public async Task Ng_values_and_AfterNg_Stop()
    {
        using var img = Frame();
        var job = new VisionJob("t") { AfterNg = AfterNg.Stop }
            .Add("a", ctx => ctx.Value("count", 3, min: 4, max: 4, code: "HOLE"))
            .Add("b", ctx => ctx.Judge(true, "X"));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ng, r.State);
        Assert.Equal(["HOLE"], r.Codes);
        Assert.Equal(StepState.Skipped, r.Steps["b"].State);
        Assert.False(r.Values[0].Ok);
    }

    [Fact]
    public async Task Timeout_waits_for_the_step_and_skips_the_rest()
    {
        using var img = Frame();
        var job = new VisionJob("t")
            .Add("slow", _ => Thread.Sleep(150), timeout: TimeSpan.FromMilliseconds(50))
            .Add("next", _ => { });
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Error, r.State);
        Assert.Contains("Timeout", r.Steps["slow"].Reason);
        Assert.True(r.Steps["slow"].Elapsed >= TimeSpan.FromMilliseconds(140));   // 끝날 때까지 기다렸다
        Assert.Equal(StepState.Skipped, r.Steps["next"].State);
    }

    [Fact]
    public async Task Cancel_token_reaches_async_steps()
    {
        using var img = Frame();
        var job = new VisionJob("t").Add("wait", async ctx => await Task.Delay(5000, ctx.Cancel), timeout: TimeSpan.FromMilliseconds(50));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Error, r.State);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Disabled_step_is_listed_and_all_disabled_is_error()
    {
        using var img = Frame();
        var job = new VisionJob("t").Add("a", _ => { }).Add("b", _ => { }).Disable("b");
        using (var r = await job.RunAsync(img))
        {
            Assert.Equal(VisionState.Ok, r.State);
            Assert.Equal(["b"], r.Disabled);
        }
        job.Disable("a");
        using (var r = await job.RunAsync(img))
            Assert.Equal(VisionState.Error, r.State);
        Assert.Throws<ArgumentException>(() => job.Disable("typo"));
    }

    [Fact]
    public async Task Busy_returns_error_at_once()
    {
        using var img = Frame();
        var gate = new TaskCompletionSource();
        var job = new VisionJob("t").Add("a", async _ => await gate.Task);
        var first = job.RunAsync(img);
        await Task.Delay(50);
        using (var busy = await job.RunAsync(img))
        {
            Assert.Equal(VisionState.Error, busy.State);
            Assert.Equal("Busy", busy.Error);
        }
        gate.SetResult();
        using var r = await first;
        Assert.Equal(VisionState.Ok, r.State);
    }

    [Fact]
    public async Task Missing_pose_skips_following_steps_with_the_cause()
    {
        using var img = Frame();
        var roi = VisionRoi.Rect(10, 10, 20, 20);
        var job = new VisionJob("t")
            .Add("align", pose: "body", ctx => ctx.Judge(false, "MARK"))
            .Add("check", () => roi, ctx => { });
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ng, r.State);                 // 원인 단계의 판정
        Assert.Equal(StepState.Skipped, r.Steps["check"].State);
        Assert.Contains("align", r.Steps["check"].Reason);

        var job2 = new VisionJob("t2")
            .Add("align", pose: "body", _ => { })              // Pose 를 안 정함
            .Add("check", () => roi, _ => { });
        using var r2 = await job2.RunAsync(img);
        Assert.Equal(VisionState.Error, r2.State);             // 검사를 안 했는데 OK 가 나가지 않게
    }

    [Fact]
    public void Follow_typo_is_caught_when_adding()
    {
        var ex = Assert.Throws<ArgumentException>(() => new VisionJob("t")
            .Add("align", pose: "body", _ => { })
            .Add("check", () => VisionRoi.Rect(0, 0, 1, 1), follow: "bdy", _ => { }));
        Assert.Contains("Declared poses: body", ex.Message);
    }

    [Fact]
    public async Task Pose_moves_the_roi()
    {
        using var img = Frame();   // 사각형 (30,30)–(70,50)
        var job = new VisionJob("t")
            .Add("align", pose: "p", ctx =>
            {
                var b = Blob.Find(ctx.Image)[0];
                ctx.SetPose(VisionPose.FromBlob(b), new VisionPose(40, 30, 0));   // 기준보다 (+10, +10)
            })
            .Add("look", () => VisionRoi.Rect(25, 25, 10, 10), ctx => ctx.Value("mean", Brightness.Mean(ctx.Image)));
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ok, r.State);
        // ROI (25,25) → (35,35): 사각형 안이라 밝다
        Assert.Equal(255, r.Values[0].Value, 1);
    }

    [Fact]
    public async Task Missing_setup_is_an_error_result()
    {
        using var img = Frame();
        var job = new VisionJob<PlateSetup>("t").Add("a", (ctx, s) => { });
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Error, r.State);
        Assert.Contains("Setup", r.Error);
    }

    [Fact]
    public async Task Result_json_has_the_judgment()
    {
        using var img = Frame();
        var job = new VisionJob("t").Add("a", ctx => ctx.Value("w", 5, max: 4, code: "W", unit: "px"));
        using var r = await job.RunAsync(img);
        var json = r.ToJson();
        Assert.Contains("\"state\":\"Ng\"", json);
        Assert.Contains("\"codes\":[\"W\"]", json);
    }

    [Fact]
    public async Task Step_image_is_kept_for_tuning()
    {
        using var img = Frame();
        var job = new VisionJob("t").Add("bin", ctx => ctx.Image.Threshold(ThresholdMode.Fixed, 100));
        using var r = await job.RunAsync(img);
        Assert.NotNull(r.Steps["bin"].Image);
        Assert.Equal(VisionPixelFormat.Gray8, r.Steps["bin"].Image!.Format);
    }
}

public class SetupTests
{
    public sealed class S
    {
        public VisionRoi Area { get; set; } = VisionRoi.Rect(1, 2, 3, 4);
        public VisionModel Mark { get; set; } = new();
        public BlobParams Blob { get; set; } = new() { Mode = ThresholdMode.Fixed };
    }

    [Fact]
    public void Copy_round_trips_and_shares_the_model_image()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        using var m = new Mat(20, 20, MatType.CV_8UC1, new Scalar(9));
        using var img = VisionImage.From(m);
        var s = new S { Mark = VisionModel.Teach(img, VisionRoi.Rect(0, 0, 10, 10), dir, "m/mark.png") };
        var copy = VisionSetup.Copy(s);
        Assert.Equal(s.Area, copy.Area);
        Assert.Equal(ThresholdMode.Fixed, copy.Blob.Mode);
        Assert.Equal("m/mark.png", copy.Mark.Path);
        Assert.Same(s.Mark.Image, copy.Mark.Image);
        Assert.NotSame(s.Mark, copy.Mark);
        Assert.True(File.Exists(Path.Combine(dir, "m", "mark.png")));

        var json = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.Contains("\"Shape\":\"Rect\"", json);
        Assert.Contains("\"Mark\":{\"Path\":\"m/mark.png\"}", json);
    }
}

// 게시 전에 굳힌 약속: 채우지 않은 상태는 OK 가 아니다 · 측정 못 한 값(NaN)도 JSON 으로 남는다 · 시간 제한 값은 넣을 때 확인한다
public class ContractTests
{
    private static VisionImage Frame()
    {
        using var m = new Mat(80, 100, MatType.CV_8UC1, Scalar.All(50));
        return VisionImage.From(m);
    }

    [Fact]
    public void A_state_that_was_never_set_is_not_ok()
    {
        Assert.NotEqual(VisionState.Ok, default);
        Assert.NotEqual(StepState.Ok, default);
        // 나쁜 쪽이 더 크다 (Worst 가 기대는 순서)
        Assert.True(VisionState.Ok < VisionState.Ng && VisionState.Ng < VisionState.Error);
    }

    [Fact]
    public async Task ToJson_writes_null_for_a_value_that_is_not_a_number()
    {
        var job = new VisionJob("j").Add("measure", ctx =>
        {
            ctx.Value("width", double.NaN, min: 1, max: 2, code: "WIDTH");
            ctx.Value("ratio", double.PositiveInfinity);
            ctx.DrawLine(new Point2d(double.NaN, 0), new Point2d(10, 10), "n/a");
        });
        using var img = Frame();
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ng, r.State);

        using var doc = System.Text.Json.JsonDocument.Parse(r.ToJson());

        var values = doc.RootElement.GetProperty("values");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, values[0].GetProperty("value").ValueKind);
        Assert.Equal(1, values[0].GetProperty("min").GetDouble());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, values[1].GetProperty("value").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("overlays")[0].GetProperty("points")[0][0].ValueKind);
    }

    [Fact]
    public async Task Timeout_values_are_checked_when_set_and_infinite_means_no_limit()
    {
        var job = new VisionJob("j");
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Timeout = TimeSpan.FromSeconds(-5));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Timeout = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Timeout = TimeSpan.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => job.Add("bad", _ => { }, timeout: TimeSpan.FromMilliseconds(-2)));
        Assert.Null(job.Timeout);

        job.Timeout = Timeout.InfiniteTimeSpan;   // .NET 의 "제한 없음"
        job.Add("a", ctx => ctx.Value("v", 1));
        using var img = Frame();
        using (var r = await job.RunAsync(img))
            Assert.True(r.Ok, r.Error);

        // 단계의 "제한 없음"은 Job 의 제한을 이긴다
        var mixed = new VisionJob("m") { Timeout = TimeSpan.FromMilliseconds(20) }
            .Add("slow but allowed", _ => Thread.Sleep(60), timeout: Timeout.InfiniteTimeSpan);
        using (var r = await mixed.RunAsync(img))
            Assert.True(r.Ok, r.Error);
    }
}

// 파일 경로는 PC 의 코드 페이지와 무관해야 한다 — 한글 경로가 영어 Windows 에서도, 그 반대도 열려야 한다
public class PathTests
{
    // 한 코드 페이지로는 다 담을 수 없는 이름 (한글 · 태국어 · 간체)
    private const string Odd = "검사 ทดสอบ 测试";

    [Fact]
    public void Images_load_and_save_in_a_folder_with_any_characters()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(dir.Path, Odd)).FullName;
        using var m = new Mat(40, 60, MatType.CV_8UC1, Scalar.All(77));
        using var img = VisionImage.From(m);

        var file = Path.Combine(folder, Odd + ".png");
        img.Save(file);
        Assert.True(File.Exists(file));
        using var back = VisionImage.Load(file);
        Assert.Equal((60, 40), (back.Width, back.Height));
        Assert.Equal(77, back.Mat.At<byte>(10, 10));

        var ex = Assert.Throws<FileNotFoundException>(() => VisionImage.Load(Path.Combine(folder, "none.png")));
        Assert.Contains("none.png", ex.Message);
        Assert.ThrowsAny<IOException>(() => img.Save(Path.Combine(folder, "no-such-folder", "a.png")));   // 폴더 없음도 IOException 의 한 종류
        Assert.Throws<IOException>(() => img.Save(Path.Combine(folder, "a.unknownformat")));
    }

    [Fact]
    public void DiffModel_saves_and_loads_in_a_folder_with_any_characters()
    {
        using var dir = new TempDir();
        static VisionImage Flat(byte v)
        {
            using var m = new Mat(20, 20, MatType.CV_8UC1, Scalar.All(v));
            return VisionImage.From(m);
        }
        using var a = Flat(100);
        using var b = Flat(104);
        using var model = DiffModel.Train([a, b]);

        model.Save(dir.Path, Odd);
        using var loaded = new DiffModel { Path = Odd };
        loaded.Load(dir.Path);

        Assert.True(loaded.IsReady);
        Assert.Equal(model.Compare(a).TotalArea, loaded.Compare(a).TotalArea);
    }
}
