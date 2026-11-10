using Devinno.Vision;
using OpenCvSharp;

public class OnnxTests
{
    private static string Tiny => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_features.onnx");

    // 질감이 있는 회색 판. 시드마다 잡음이 조금씩 다르다 (양품끼리의 차이)
    private static VisionImage Plate(int seed, Action<Mat>? defect = null)
    {
        var rnd = new Random(seed);
        var px = new byte[64 * 64 * 3];
        for (var i = 0; i < px.Length; i++)
            px[i] = (byte)(115 + (i % 3) * 10 + rnd.Next(-12, 13));
        using var m = Mat.FromPixelData(64, 64, MatType.CV_8UC3, px).Clone();
        for (var x = 0; x < 64; x += 8)
            Cv2.Line(m, new Point(x, 0), new Point(x, 63), new Scalar(140, 150, 160), 1);   // 결 무늬
        defect?.Invoke(m);
        return VisionImage.From(m);
    }

    [Fact]
    public void OnnxModel_runs_and_reports_shapes()
    {
        using var model = OnnxModel.Load(Tiny, 64, 64);
        Assert.Equal(["a", "b"], model.Outputs);
        using var img = Plate(1);
        var o = model.Run(img);
        Assert.Equal([1, 8, 16, 16], o["a"].Shape);
        Assert.Equal([1, 16, 8, 8], o["b"].Shape);
        Assert.Equal(8 * 16 * 16, o["a"].Data.Length);
        Assert.Throws<KeyNotFoundException>(() => o["c"]);
    }

    [Fact]
    public void Missing_model_file_is_explained()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => OnnxModel.Load("nope.onnx", 64, 64));
        Assert.Contains("nope.onnx", ex.Message);
    }

    [Fact]
    public void AnomalyModel_scores_a_defect_higher_and_points_at_it()
    {
        using var features = OnnxModel.Load(Tiny, 64, 64);
        var goods = Enumerable.Range(0, 12).Select(i => Plate(i)).ToList();
        var model = AnomalyModel.Train(features, goods, representatives: 16);
        goods.ForEach(g => g.Dispose());
        Assert.Equal(16, model.GridWidth);   // 가장 촘촘한 출력 'a' 의 격자
        Assert.Equal(8 + 16, model.Dimension);

        using var good = Plate(100);
        using var bad = Plate(101, m => Cv2.Rectangle(m, new Rect(40, 8, 10, 10), Scalar.All(20), -1));   // 어두운 얼룩
        var g = model.Score(good);
        var b = model.Score(bad);
        Assert.True(b.Score > g.Score * 1.5, $"defect {b.Score:0.00} vs good {g.Score:0.00}");
        Assert.InRange(b.Region.X + b.Region.Width / 2, 32, 58);   // 가장 이상한 칸이 얼룩 근처
        Assert.InRange(b.Region.Y + b.Region.Height / 2, 0, 26);
    }

    [Fact]
    public void AnomalyModel_save_load_and_setup_copy()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var features = OnnxModel.Load(Tiny, 64, 64);
        var goods = Enumerable.Range(0, 6).Select(i => Plate(i)).ToList();
        using var model = AnomalyModel.Train(features, goods, representatives: 8);
        goods.ForEach(g => g.Dispose());
        model.Save(dir, "front/anomaly");
        Assert.True(File.Exists(Path.Combine(dir, "front", "anomaly", "features.onnx")));

        using var loaded = new AnomalyModel { Path = "front/anomaly" };
        loaded.Load(dir);
        using var img = Plate(50);
        Assert.Equal(model.Score(img).Score, loaded.Score(img).Score, 4);

        var copy = VisionSetup.Copy(new Holder { Anomaly = model });
        Assert.True(copy.Anomaly.IsReady);
        Assert.Equal("front/anomaly", copy.Anomaly.Path);
    }

    [Fact]
    public async Task Works_inside_a_job_and_draws()
    {
        using var features = OnnxModel.Load(Tiny, 64, 64);
        var goods = Enumerable.Range(0, 8).Select(i => Plate(i)).ToList();
        using var model = AnomalyModel.Train(features, goods, representatives: 8);
        goods.ForEach(g => g.Dispose());
        var job = new VisionJob("t").Add("표면", ctx =>
        {
            var a = model.Score(ctx.Image);
            ctx.Draw(a);
            ctx.Value("이상 점수", a.Score);
        });
        using var img = Plate(77);
        using var r = await job.RunAsync(img);
        Assert.Equal(VisionState.Ok, r.State);
        Assert.Contains(r.Overlays, o => o.Kind == VisionOverlayKind.Box);
    }

    public sealed class Holder { public AnomalyModel Anomaly { get; set; } = new(); }
}
