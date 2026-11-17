using Devinno.Vision;
using Devinno.Vision.Recipes;
using OpenCvSharp;

// AI 문서(llm-rules.md · vision-*.md)의 예제 코드가 컴파일되고 문서가 말한 대로 도는지.
// 문서의 예제를 고치면 여기도 같이 고친다 (같은 코드를 Plate 샘플 · 테스트용 작은 모델로 돌린다).
public class DocExamplesTests
{
    private static string Recipe => Path.Combine(AppContext.BaseDirectory, "Recipes", "Plate");
    private static string Good => PlateInspection.Sample("01_good.bmp");
    private static string MissingHole => PlateInspection.Sample("02_missinghole.bmp");
    private static string TinyFeatures => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_features.onnx");

    // ── llm-rules.md §3: 레시피 실행 ──
    [Fact]
    public async Task Rules_run_a_recipe()
    {
        using var recipe = VisionRecipe.Load(Recipe);
        var job = recipe.CreateJob();

        using (var img = VisionImage.Load(Good))
        using (var r = await job.RunAsync(img))
        {
            Assert.Equal(VisionState.Ok, r.State);
            Assert.True(r.Ok);
            Assert.Empty(r.Codes);
            var lines = r.Values.Select(v => $"{v.Name} = {v.Value:0.##} {v.Unit} ({(v.Ok ? "OK" : v.Code)})").ToList();
            Assert.NotEmpty(lines);
        }
        using (var img = VisionImage.Load(MissingHole))
        using (var r = await job.RunAsync(img))
        {
            Assert.Equal(VisionState.Ng, r.State);
            Assert.Contains("HOLE", r.Codes);
            Assert.Null(r.Error);
        }
    }

    // ── llm-rules.md §3: 코드로 짠 검사 ──
    [Fact]
    public async Task Rules_code_first_job()
    {
        var job = new VisionJob("plate")
            .Add("align", pose: "body", ctx =>
            {
                var part = Blob.Find(ctx.Image, minArea: 20000).FirstOrDefault();
                if (part is null) { ctx.Fail("part not found"); return; }
                ctx.SetPose(VisionPose.FromBlob(part), reference: new VisionPose(319.1, 240.7, -2.3));
            })
            .Add("left hole", roi: () => VisionRoi.Rect(138, 130, 60, 60), ctx =>
            {
                var holes = Blob.Find(ctx.Image, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
                ctx.Draw(holes);
                ctx.Value("left holes", holes.Length, min: 1, max: 1, code: "HOLE");
            })
            .Add("length", roi: () => VisionRoi.Rect(100, 180, 440, 12), ctx =>
            {
                var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject, minContrast: 8);
                ctx.Draw(e);
                ctx.Value("length", e.Distance, nominal: 400, plus: 5, minus: 5, code: "LENGTH", unit: "px");
            });

        using (var img = VisionImage.Load(Good))
        using (var r = await job.RunAsync(img))
            Assert.True(r.Ok, r.ToJson(indented: true));
        using (var img = VisionImage.Load(MissingHole))
        using (var r = await job.RunAsync(img))
            Assert.Equal(["HOLE"], r.Codes);

        // 부품이 없는 이미지는 OK 가 아니라 Error (§7.6)
        using var blank = new Mat(480, 640, MatType.CV_8UC1, Scalar.All(0));
        using var none = VisionImage.From(blank);
        using var rn = await job.RunAsync(none);
        Assert.Equal(VisionState.Error, rn.State);
        Assert.Equal("plate/align: part not found", rn.Error);
        Assert.Equal(StepState.Skipped, rn.Steps["left hole"].State);
        Assert.StartsWith("Pose 'body' missing", rn.Steps["left hole"].Reason);
    }

    // ── llm-rules.md §7: 폴더의 이미지로 검증 ──
    [Fact]
    public async Task Rules_verify_with_a_file_camera()
    {
        using var recipe = VisionRecipe.Load(Recipe);
        var job = recipe.CreateJob();
        var lines = new List<string>();
        await using var camera = new FileCamera(PlateInspection.SampleDir, loop: false);
        foreach (var _ in camera.Files)
        {
            using var img = await camera.Grab(default);
            using var r = await job.RunAsync(img);
            lines.Add($"{Path.GetFileName(camera.LastFile)}: {r.State} [{string.Join(", ", r.Codes)}] {r.Error}");
            var good = Path.GetFileName(camera.LastFile)!.Contains("_good");
            Assert.Equal(good ? VisionState.Ok : VisionState.Ng, r.State);
        }
        Assert.Equal(18, lines.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.Grab(default));
    }

    // ── llm-rules.md §4: 같은 Job 을 겹쳐 부르면 Busy ──
    [Fact]
    public async Task Rules_busy_and_keep()
    {
        var gate = new TaskCompletionSource();
        VisionImage? kept = null;
        VisionImage? lost = null;
        var job = new VisionJob("j").Add("wait", async ctx =>
        {
            lost = ctx.Image.Gray();
            kept = ctx.Image.Gray().Keep();
            ctx.Image.Blur(3);                 // 단계가 만든 마지막 이미지는 결과가 가진다 (r.Steps["wait"].Image)
            await gate.Task;
        });
        using var img = VisionImage.Load(Good);
        var first = job.RunAsync(img);
        using (var busy = await job.RunAsync(img))
        {
            Assert.Equal(VisionState.Error, busy.State);
            Assert.Equal("Busy", busy.Error);
        }
        gate.SetResult();
        using var r = await first;
        Assert.True(r.Ok);
        Assert.NotNull(r.Steps["wait"].Image);
        var ex = Assert.Throws<ObjectDisposedException>(() => lost!.Mat);
        Assert.Contains("released when the run of 'j' ended", ex.Message);
        using (kept)
            Assert.Equal(img.Width, kept!.Width);
    }

    // ── vision-recipe.md: 결과 읽기 · 오버레이 그리기 · 이름 ──
    [Fact]
    public async Task Recipe_result_names_and_overlay_picture()
    {
        using var dir = new TempDir();
        using var recipe = VisionRecipe.Load(Recipe);
        var job = recipe.CreateJob();
        Assert.Equal("플레이트", job.Name);
        using var img = VisionImage.Load(MissingHole);
        using var r = await job.RunAsync(img);

        var failed = r.Values.Where(v => !v.Ok).Select(v => $"{v.Step}: {v.Name} = {v.Value:0.##} (limits {v.Min}–{v.Max}) → {v.Code}").ToList();
        Assert.NotEmpty(failed);
        Assert.Contains(r.Values, v => v.Name == "왼쪽 구멍 개수" && v.Step == "왼쪽 구멍");
        Assert.Contains(r.Values, v => v.Name == "노치 픽셀");
        Assert.Contains(r.Values, v => v.Name == "길이" && v.Unit == "mm");
        Assert.True(r.Poses.ContainsKey(VisionRecipe.PoseName));
        Assert.Equal(StepState.Ok, r.Steps["길이"].State);
        Assert.Throws<KeyNotFoundException>(() => r.Steps["no such step"]);
        Assert.Contains("\"state\":\"Ng\"", r.ToJson());

        var file = Path.Combine(dir.Path, "result.png");
        SaveResultPicture(r, file);
        Assert.True(new FileInfo(file).Length > 1000);

        // 부품이 없으면: 정렬 검사의 문구 그대로
        using var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        using var none = VisionImage.From(blank);
        using var rn = await job.RunAsync(none);
        Assert.Equal("플레이트/정렬: 부품 없음", rn.Error);
    }

    private static void SaveResultPicture(VisionResult r, string file)
    {
        using var canvas = new Mat();
        if (r.Image.Format == VisionPixelFormat.Gray8)
            Cv2.CvtColor(r.Image.Mat, canvas, ColorConversionCodes.GRAY2BGR);
        else
            r.Image.Mat.CopyTo(canvas);

        foreach (var o in r.Overlays)
        {
            var color = r.Steps[o.Step].State switch
            {
                StepState.Ok => new Scalar(0, 200, 0),
                StepState.Ng => new Scalar(0, 0, 255),
                _ => new Scalar(0, 165, 255),
            };
            var pts = o.Points.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
            switch (o.Kind)
            {
                case VisionOverlayKind.Roi:
                case VisionOverlayKind.Box:
                case VisionOverlayKind.Contour:
                    Cv2.Polylines(canvas, new[] { pts }, isClosed: true, color, thickness: 2);
                    break;
                case VisionOverlayKind.Line:
                    Cv2.Line(canvas, pts[0], pts[1], color, thickness: 2);
                    break;
                case VisionOverlayKind.Point:
                    Cv2.Circle(canvas, pts[0], 4, color, thickness: -1);
                    break;
            }
            if (o.Label is { } label)
                Cv2.PutText(canvas, label, pts[0], HersheyFonts.HersheySimplex, 0.5, color);
        }
        Cv2.ImWrite(file, canvas);
    }

    // ── vision-recipe.md: 검사 읽기 · 코드 단계 덧붙이기 · 패키지 ──
    [Fact]
    public async Task Recipe_checks_appended_step_and_package()
    {
        using var dir = new TempDir();
        using var recipe = VisionRecipe.Load(Recipe, loadModels: false);
        var lines = recipe.Checks.Select(c => $"{c.Name} ({c.Kind}, {c.Role}) roi {c.Roi.Shape} {c.Roi.Width}×{c.Roi.Height}").ToList();
        Assert.Equal(5, lines.Count);
        var len = recipe.Checks.OfType<LengthCheck>().First();
        Assert.Equal("길이: 84 +1 / -1", $"{len.Name}: {len.Nominal} +{len.Plus} / -{len.Minus}");
        using (var baseImage = recipe.LoadBaseImage())
            Assert.True(baseImage.Width > 0);

        // CreateJob 이 준 Job 에 코드 단계를 덧붙이면 레시피 검사 뒤에 돌고, ROI 는 레시피의 Pose 를 따라간다
        var job = recipe.CreateJob();
        job.Add("extra", roi: () => VisionRoi.Rect(138, 130, 60, 60), ctx => ctx.Value("extra mean", Brightness.Mean(ctx.Image), max: 255));
        using (var img = VisionImage.Load(Good))
        using (var r = await job.RunAsync(img))
        {
            Assert.True(r.Ok);
            Assert.Equal("extra", r.Steps[^1].Name);
            Assert.Equal(VisionRecipe.PoseName, r.Steps["extra"].Followed);
        }

        var file = Path.Combine(dir.Path, "plate.dvpkg");
        int files = recipe.Pack(file);
        Assert.Equal(2, files);
        using var onSite = VisionRecipe.Load(file);
        Assert.Equal(Path.GetFullPath(file), onSite.PackagePath);
        var packed = onSite.CreateJob();
        using var img2 = VisionImage.Load(Good);
        using var r2 = await packed.RunAsync(img2);
        Assert.True(r2.Ok);
        Assert.Throws<ArgumentException>(() => recipe.Pack(Path.Combine(dir.Path, "plate.zip")));
    }

    // ── vision-recipe.md: 양품 모델 학습 · 사유 검사 ──
    [Fact]
    public async Task Recipe_good_model_training()
    {
        using var dir = new TempDir();
        File.Copy(Path.Combine(Recipe, VisionRecipe.FileName), Path.Combine(dir.Path, VisionRecipe.FileName));
        Directory.CreateDirectory(Path.Combine(dir.Path, "images"));
        File.Copy(Path.Combine(Recipe, "images", "base.bmp"), Path.Combine(dir.Path, "images", "base.bmp"));

        using var recipe = VisionRecipe.Load(dir.Path, loadModels: false);
        var stages = new HashSet<string>();
        var result = await recipe.TrainGoodModelAsync(new GoodModelTraining
        {
            Images = Directory.GetFiles(PlateInspection.SampleDir, "*_good.bmp"),
            Features = TinyFeatures,
            Roi = VisionRoi.Rect(100, 150, 470, 250),
            InputSize = 64,          // 테스트용 작은 특징 모델의 입력 크기
            Representatives = 8,
        }, new SyncProgress<GoodModelProgress>(p => stages.Add(p.Stage)));
        recipe.Save();
        var line = $"learned {result.Learned}, limit {result.Model.Max}, {result.MsPerImage:0} ms per image, skipped {result.Skipped.Count}";
        Assert.StartsWith("learned 8, limit ", line);
        Assert.Equal(["crop", "learn", "limit"], stages.Order());
        Assert.Equal(2, recipe.FormatVersion);
        Assert.True(recipe.GoodModelIsCurrent);

        var job = recipe.CreateJob();
        using var img = VisionImage.Load(Good);
        using var r = await job.RunAsync(img);
        Assert.Contains(r.Values, v => v.Name == "양품 모델 점수" && v.Step == VisionRecipe.GoodModelStepName && v.Code == "SURFACE");
        using var crop = await recipe.CropAsync(img, VisionRoi.Rect(100, 150, 470, 250));
        Assert.Equal((470, 250), (crop.Width, crop.Height));

        // 정렬 설정이 바뀌면 다시 학습해야 한다
        ((BlobAlignCheck)recipe.Checks[0]).Blob.MinArea = 25000;
        Assert.False(recipe.GoodModelIsCurrent);
        Assert.Throws<InvalidOperationException>(() => recipe.CreateJob());
    }

    // ── vision-jobs.md: 설정 클래스 · mm · 티칭 · 설정 바꾸기 ──
    public sealed class PlateDocSetup
    {
        public BlobParams Body { get; set; } = new() { MinArea = 20000 };
        public VisionPose? BodyRef { get; set; }
        public VisionRoi HoleL { get; set; } = VisionRoi.Rect(138, 130, 60, 60);
        public BlobParams Hole { get; set; } = new() { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 };
        public VisionRoi Width { get; set; } = VisionRoi.Rect(100, 180, 440, 12);
        public double WidthNominal { get; set; } = 100;
    }

    [Fact]
    public async Task Jobs_setup_class_teaching_and_copy()
    {
        var job = new VisionJob<PlateDocSetup>("plate") { Setup = new PlateDocSetup() }
            .Add("align", pose: "body", (ctx, s) =>
            {
                var part = Blob.Find(ctx.Image, s.Body).FirstOrDefault();
                if (part is null) { ctx.Fail("part not found"); return; }
                ctx.Draw(part);
                ctx.SetPose(VisionPose.FromBlob(part, s.BodyRef), s.BodyRef);
            })
            .Add("left hole", roi: s => s.HoleL, (ctx, s) =>
            {
                var holes = Blob.Find(ctx.Image, s.Hole);
                ctx.Draw(holes);
                ctx.Value("left holes", holes.Length, min: 1, max: 1, code: "HOLE");
            })
            .Add("width", roi: s => s.Width, (ctx, s) =>
            {
                var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject, minContrast: 8);
                ctx.Draw(e);
                ctx.Value("width", ctx.Mm(e), nominal: s.WidthNominal, plus: 1, minus: 1, code: "WIDTH", unit: "mm");
            });
        job.Calibration = VisionCalibration.Scale(0.25);

        // 티칭: 이 이미지가 기준
        var s = job.Setup!;
        s.BodyRef = null;
        using (var baseImage = VisionImage.Load(Good))
        using (var r = await job.RunAsync(baseImage))
        {
            Assert.True(r.Ok, r.ToJson(indented: true));
            if (r.Poses.TryGetValue("body", out var pose))
                s.BodyRef = pose;
        }
        Assert.NotNull(s.BodyRef);

        // 돌아간(회전된) 양품도 ROI 가 따라가서 OK
        using (var img = VisionImage.Load(PlateInspection.Sample("18_good.bmp")))
        using (var r = await job.RunAsync(img))
        {
            Assert.True(r.Ok, r.ToJson(indented: true));
            Assert.Equal("body", r.Steps["width"].Followed);
            Assert.Equal(100, r.Values.Single(v => v.Name == "width").Nominal);
        }

        // 설정 바꾸기: 사본을 고쳐 통째로 바꾼다
        var edit = VisionSetup.Copy(job.Setup!);
        edit.Hole.Threshold = 90;
        Assert.Equal(80, job.Setup!.Hole.Threshold);
        job.Setup = edit;
        Assert.Equal(s.BodyRef, edit.BodyRef);

        // 캘리브레이션 없이 ctx.Mm → 그 단계가 Error
        job.Calibration = null;
        using var img2 = VisionImage.Load(Good);
        using var r2 = await job.RunAsync(img2);
        Assert.Equal(VisionState.Error, r2.State);
        Assert.StartsWith("plate/width: The job has no calibration", r2.Error);
    }

    public sealed class FaceSetup { public VisionRoi Width { get; set; } = VisionRoi.Rect(100, 180, 440, 12); }
    public sealed class PartData { public double? TopWidth; public double? SideWidth; }
    public sealed class LabelSetup { public VisionModel Logo { get; set; } = new(); public VisionRoi LogoRoi { get; set; } }

    [Fact]
    public async Task Jobs_part_data_opencv_and_reference_image()
    {
        var top = new VisionJob<FaceSetup, PartData>("top") { Setup = new FaceSetup() }
            .Add("width", roi: s => s.Width, (ctx, s, d) => { d.TopWidth = 12.3; });
        var side = new VisionJob<FaceSetup, PartData>("side") { Setup = new FaceSetup() }
            .Add("width", roi: s => s.Width, (ctx, s, d) => { d.SideWidth = 4.5; });
        using var image = VisionImage.Load(Good);
        var data = new PartData();
        using (var r1 = await top.RunAsync(image, data))
        using (var r2 = await side.RunAsync(image, data))
            Assert.Equal(VisionState.Ok, VisionResult.Worst(r1, r2));
        Assert.Equal((12.3, 4.5), (data.TopWidth, data.SideWidth));
        await Assert.ThrowsAsync<ArgumentNullException>(() => top.RunAsync(image, null!));

        // OpenCV 로 내려갔다 올라오기
        var roi = VisionRoi.Rect(138, 130, 60, 60);
        var job = new VisionJob("cv").Add("edges", roi: () => roi, ctx =>
        {
            using var edges = new Mat();
            Cv2.Canny(ctx.Image.Mat, edges, 50, 150);
            var img = ctx.From(edges);
            ctx.Value("edge pixels", Cv2.CountNonZero(edges), max: 5000, code: "EDGE");
            ctx.DrawBox(new Rect(10, 10, 20, 20), "here", from: img);
        });
        using (var r = await job.RunAsync(image))
        {
            Assert.True(r.Ok, r.ToJson(indented: true));
            var box = r.Overlays.Single(o => o.Kind == VisionOverlayKind.Box);
            Assert.Equal(new Point2d(148, 140), box.Points[0]);   // ROI 왼위 (138, 130) + (10, 10)
        }

        // 기준 이미지 (VisionModel)
        using var dir = new TempDir();
        var labels = new VisionJob<LabelSetup>("label") { Setup = new LabelSetup { LogoRoi = VisionRoi.Rect(238, 116, 36, 20) } };
        var edit = VisionSetup.Copy(labels.Setup!);
        edit.Logo = VisionModel.Teach(image, edit.LogoRoi, dir.Path, "models/logo.png");
        labels.Setup = edit;
        var setup = System.Text.Json.JsonSerializer.Deserialize<LabelSetup>(System.Text.Json.JsonSerializer.Serialize(edit))!;
        Assert.Equal("models/logo.png", setup.Logo.Path);
        Assert.Null(setup.Logo.Image);
        setup.Logo.Load(dir.Path);
        Assert.Equal(36, setup.Logo.Image!.Width);
        setup.Logo.Image.Dispose();   // 테스트 뒷정리 (앱은 해제하지 않는다)
        edit.Logo.Image!.Dispose();
    }

    // ── vision-jobs.md: 실행이 끝나는 방식 표 ──
    [Fact]
    public async Task Jobs_run_rules()
    {
        using var image = VisionImage.Load(Good);
        var job = new VisionJob("j")
            .Add("a", ctx => ctx.Judge(false, "A"))
            .Add("b", ctx => throw new InvalidOperationException("boom"))
            .Add("c", ctx => { });
        using (var r = await job.RunAsync(image))
        {
            Assert.Equal(VisionState.Error, r.State);           // Error 가 NG 보다 우선
            Assert.Equal("j/b: boom", r.Error);
            Assert.Equal(StepState.Ok, r.Steps["c"].State);     // 예외 뒤 단계도 돈다
        }
        job.Disable("b");
        job.AfterNg = AfterNg.Stop;
        using (var r = await job.RunAsync(image))
        {
            Assert.Equal(VisionState.Ng, r.State);
            Assert.Equal(["A"], r.Codes);
            Assert.Equal(["b"], r.Disabled);
            Assert.Equal("Disabled", r.Steps["b"].Reason);
            Assert.Equal("Stopped after NG in a", r.Steps["c"].Reason);
        }
        Assert.Throws<ArgumentException>(() => job.Disable("nope"));
        Assert.Throws<ArgumentException>(() => job.Add("a", ctx => { }));

        var slow = new VisionJob("s") { Timeout = TimeSpan.FromMilliseconds(30) }
            .Add("slow", async ctx => await Task.Delay(5000, ctx.Cancel))
            .Add("after", ctx => { });
        using (var r = await slow.RunAsync(image))
        {
            Assert.Equal(VisionState.Error, r.State);
            Assert.StartsWith("Timeout (limit 30 ms", r.Steps["slow"].Reason);
            Assert.Equal("Skipped after timeout in slow", r.Steps["after"].Reason);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = new VisionJob("c").Add("x", async ctx => await Task.Delay(5000, ctx.Cancel)).Add("y", ctx => { });
        using (var r = await canceled.RunAsync(image, cts.Token))
        {
            Assert.Equal(VisionState.Error, r.State);
            Assert.Equal("Canceled", r.Steps["x"].Reason);
            Assert.Equal("Canceled", r.Steps["y"].Reason);
        }

        using (var r = await new VisionJob("empty").RunAsync(image))
            Assert.Equal("empty: the job has no steps.", r.Error);
        using (var r = await new VisionJob<FaceSetup>("nosetup").Add("x", (ctx, s) => { }).RunAsync(image))
            Assert.Equal("nosetup: Setup is not set.", r.Error);
        using (var r = await new VisionJob("roi").Add("x", roi: () => default, ctx => { }).RunAsync(image))
            Assert.Equal("roi/x: The ROI is not set.", r.Error);
    }

    // ── vision-toolkit.md: 블롭 · 밝기 · 캘리퍼 · 둥근 부품 · 차분 ──
    [Fact]
    public async Task Toolkit_tools()
    {
        using var image = VisionImage.Load(Good);
        using var hole = image.Crop(VisionRoi.Rect(138, 130, 60, 60));
        Blob[] blobs = Blob.Find(hole, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
        Blob[] same = Blob.Find(hole, new BlobParams { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 });
        Assert.Single(blobs);
        Assert.Equal(blobs[0].Area, same[0].Area);
        Assert.InRange(blobs[0].Angle, -90, 90);
        Assert.InRange(Brightness.Mean(hole), 0, 255);
        Assert.True(Brightness.StdDev(hole) > 0);
        Assert.True(Brightness.Count(hole, 0, 100) > 0);

        using var band = image.Crop(VisionRoi.Rect(100, 180, 440, 12));
        var e = Edge.Caliper(band, EdgePolarity.BrightObject, minContrast: 8);
        Assert.True(e.Found);
        Assert.Equal(e.P2.X - e.P1.X, e.Distance);
        using var flat = new Mat(30, 200, MatType.CV_8UC1, Scalar.All(100));
        using var flatImage = VisionImage.From(flat);
        var none = Edge.Caliper(flatImage);
        Assert.False(none.Found);
        Assert.True(double.IsNaN(none.Distance));

        // RoundModel
        using var dir = new TempDir();
        var model = RoundModel.Train(image, center: new Point2d(330, 256), innerRadius: 60, outerRadius: 200);
        model.Save(Path.Combine(dir.Path, "round.json"));
        model = RoundModel.Load(Path.Combine(dir.Path, "round.json"));
        var job = new VisionJob("round").Add("align", pose: "nut", ctx =>
        {
            var match = model.Find(ctx.Image, new Point2d(330, 256));
            ctx.Value("match", match.Score, min: 0.5, code: "ALIGN");
            ctx.Judge(!match.Mirrored, "FLIPPED");
            ctx.SetPose(match.Pose, model.ReferencePose);
        });
        using (var r = await job.RunAsync(image))
        {
            Assert.True(r.Ok, r.ToJson(indented: true));
            Assert.InRange(r.Poses["nut"].Angle, -1, 1);
        }
        Assert.Throws<ArgumentException>(() => RoundModel.Train(image, new Point2d(330, 256), 50, 50));

        // DiffModel
        var goodCrops = Directory.GetFiles(PlateInspection.SampleDir, "*_good.bmp").Take(4).Select(f =>
        {
            using var g = VisionImage.Load(f);
            return g.Crop(VisionRoi.Rect(238, 116, 36, 20));
        }).ToList();
        try
        {
            var diff = DiffModel.Train(goodCrops, color: false);
            diff.Save(dir.Path, "models/diff");
            Assert.Equal("models/diff", diff.Path);
            var loaded = new DiffModel { Path = "models/diff" };
            Assert.False(loaded.IsReady);
            loaded.Load(dir.Path);
            var d = loaded.Compare(goodCrops[0], k: 3.5, minSpread: 6, minArea: 10);
            Assert.Equal(d.Regions.Length > 0 ? d.Regions[0].Area : 0, d.MaxArea);
            Assert.Throws<ArgumentException>(() => loaded.Compare(image));
            Assert.Throws<InvalidOperationException>(() => new DiffModel().Compare(image));
        }
        finally
        {
            goodCrops.ForEach(c => c.Dispose());
        }
    }

    // ── vision-toolkit.md: ONNX · 이상 탐지 · 불량 분류 · 카메라 ──
    [Fact]
    public async Task Toolkit_models_and_cameras()
    {
        using var dir = new TempDir();
        using var image = VisionImage.Load(Good);

        using (var model = OnnxModel.Load(TinyFeatures, inputWidth: 64, inputHeight: 64, VisionTensorFormat.ImageNet))
        {
            OnnxOutputs o = model.Run(image);
            OnnxTensor first = o.Ordered[0];
            Assert.Same(first, o[model.Outputs[0]]);
            float[] values = first.Data;
            int[] shape = first.Shape;
            Assert.Equal(4, shape.Length);
            Assert.Equal(values.Length, shape.Aggregate(1, (a, b) => a * b));
            Assert.Equal(3 * 64 * 64, image.ToTensor(64, 64, VisionTensorFormat.ImageNet).Length);
        }
        Assert.Throws<FileNotFoundException>(() => OnnxModel.Load(Path.Combine(dir.Path, "none.onnx"), 64, 64));

        var goodCrops = Directory.GetFiles(PlateInspection.SampleDir, "*_good.bmp").Take(4).Select(VisionImage.Load).ToList();
        try
        {
            var features = OnnxModel.Load(TinyFeatures, 64, 64);
            using var model = AnomalyModel.Train(features, goodCrops, representatives: 8);
            model.Save(dir.Path, "models/surface");
            using var loaded = new AnomalyModel { Path = "models/surface" };
            Assert.False(loaded.IsReady);
            loaded.Load(dir.Path);
            var a = loaded.Score(image);
            Assert.Equal(a.GridWidth * a.GridHeight, a.Map.Length);
            Assert.Equal(a.Map.Max(), a.Score, 5);
            using var blank = new AnomalyModel();
            Assert.Throws<InvalidOperationException>(() => blank.Score(image));
        }
        finally
        {
            goodCrops.ForEach(c => c.Dispose());
        }

        var folder = DefectModelTests.ModelFolder(dir.Path, "models/defect-1");
        using (var model = DefectModel.Load(folder))
        {
            DefectResult d = model.Classify(image);
            Assert.Equal(1 - d.Probabilities[model.GoodLabel], d.DefectProbability, 9);
            Assert.NotEqual(model.GoodLabel, d.Defect);
            Assert.Contains(d.Defect.ToUpperInvariant(), new[] { "SCRATCH", "DENT" });
        }
        Assert.Throws<FileNotFoundException>(() => DefectModel.Load(Path.Combine(dir.Path, "none")));

        // 카메라: 이 드라이버들은 하드웨어 트리거 · 노출 · 게인이 없다
        await using var camera = new FileCamera(PlateInspection.SampleDir);
        Assert.True(double.IsNaN(camera.ExposureUs));
        Assert.Throws<NotSupportedException>(() => camera.Gain = 1);
        Assert.Throws<DirectoryNotFoundException>(() => new FileCamera(Path.Combine(dir.Path, "none")));
        Assert.Throws<FileNotFoundException>(() => new FileCamera(dir.Path));
    }
}

// ── XML 주석의 <example> 가운데 문서 예제와 겹치지 않는 것 ──
public class XmlExamplesTests
{
    public sealed class HoleCount(string name, int expected) : IVisionStep
    {
        public string Name { get; } = name;

        public Task RunAsync(VisionContext ctx)
        {
            var holes = Blob.Find(ctx.Image, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
            ctx.Draw(holes);
            ctx.Value(Name, holes.Length, min: expected, max: expected, code: "HOLE");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Step_object_keep_overlays_and_images()
    {
        using var dir = new TempDir();
        var roi = VisionRoi.Rect(138, 130, 60, 60);
        VisionImage? binary = null;
        var job = new VisionJob("x");
        job.Add(new HoleCount("left hole", 1), roi: () => VisionRoi.Rect(138, 130, 60, 60));
        job.Add("holes", roi: () => roi, ctx =>
        {
            binary = ctx.Image.Threshold(ThresholdMode.Fixed, 80, invert: true).Keep();
        });

        using var img = VisionImage.Load(PlateInspection.Sample("01_good.bmp"));
        using (var r = await job.RunAsync(img))
        {
            Assert.True(r.Ok, r.ToJson(indented: true));

            // VisionResult.Overlays 예제
            using var canvas = r.Image.Mat.Clone();
            foreach (var o in r.Overlays)
            {
                var pts = o.Points.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
                var color = r.Steps[o.Step].State == StepState.Ok ? new Scalar(0, 200, 0) : new Scalar(0, 0, 255);
                if (pts.Length >= 3) Cv2.Polylines(canvas, new[] { pts }, isClosed: true, color, thickness: 2);
                else if (pts.Length == 2) Cv2.Line(canvas, pts[0], pts[1], color, thickness: 2);
                else Cv2.Circle(canvas, pts[0], 4, color, thickness: -1);
            }
            Assert.True(Cv2.ImWrite(Path.Combine(dir.Path, "result.png"), canvas));
        }
        using (binary)
        {
            Assert.Equal(VisionPixelFormat.Gray8, binary!.Format);
            binary.Save(Path.Combine(dir.Path, "binary.png"));
        }

        // VisionImage 클래스 예제: 실행 밖에서 만든 파생 이미지는 부른 쪽 것
        using var gray = img.Gray();
        using var small = gray.Resize(320, 240);
        Cv2.GaussianBlur(small.Mat, small.Mat, new Size(5, 5), 0);
        var inOriginal = small.ToSource(new Point2d(10, 10));
        Assert.InRange(inOriginal.X, 10, img.Width);

        // VisionImage.From 예제
        using var mat = Cv2.ImRead(PlateInspection.Sample("01_good.bmp"), ImreadModes.Grayscale);
        using var wrapped = VisionImage.From(mat, new VisionImageInfo { Camera = "cam1", FrameNo = 42 });
        Assert.Equal(42, wrapped.Info.FrameNo);
        using var bad = new Mat(4, 4, MatType.CV_16UC1);
        Assert.Throws<NotSupportedException>(() => VisionImage.From(bad));
        using var empty = new Mat();
        Assert.Throws<ArgumentException>(() => VisionImage.From(empty));
    }
}

// 진행률을 부른 스레드에서 바로 받는다 (Progress<T> 는 다른 스레드로 넘겨 테스트가 끝난 뒤에 올 수 있다)
file sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
