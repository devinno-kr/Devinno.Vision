using Devinno.Vision;
using OpenCvSharp;

public class RoundModelTests
{
    // 날개 하나가 긴 비대칭 바람개비 (회전 · 거울상을 구분할 수 있게)
    private static VisionImage Pinwheel(double angleDeg, bool mirror = false)
    {
        using var m = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(20));
        var c = new Point2d(150, 150);
        for (var k = 0; k < 3; k++)
        {
            var a = (angleDeg + k * 120) * Math.PI / 180;
            var len = k == 0 ? 120 : 90;
            // 날개: 중심에서 바깥으로, 끝이 한쪽으로 굽음
            Point2d P(double r, double da) => new(c.X + r * Math.Cos(a + da), c.Y + r * Math.Sin(a + da));
            var pts = new[] { P(30, -0.25), P(len, -0.05), P(len, 0.25), P(30, 0.25) }.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
            Cv2.FillConvexPoly(m, pts, Scalar.All(200));
        }
        Cv2.Circle(m, new Point(150, 150), 25, Scalar.All(0), -1);
        if (mirror)
            Cv2.Flip(m, m, FlipMode.Y);
        return VisionImage.From(m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(-100)]
    [InlineData(170)]
    public void Finds_rotation_clockwise_positive(double angle)
    {
        using var b = Pinwheel(0);
        var model = RoundModel.Train(b, new Point2d(150, 150), 40, 125);
        using var img = Pinwheel(angle);
        var m = model.Find(img, new Point2d(150, 150));
        Assert.False(m.Mirrored);
        Assert.InRange(VisionPose.Normalize(m.Pose.Angle - angle), -1.0, 1.0);
        Assert.True(m.Score > 0.9);
    }

    [Fact]
    public void Detects_mirrored_part()
    {
        using var b = Pinwheel(0);
        var model = RoundModel.Train(b, new Point2d(150, 150), 40, 125);
        using var img = Pinwheel(25, mirror: true);
        Assert.True(model.Find(img, new Point2d(150, 150)).Mirrored);
    }

    [Fact]
    public void Json_round_trip()
    {
        using var b = Pinwheel(0);
        var model = RoundModel.Train(b, new Point2d(150, 150), 40, 125);
        var file = Path.GetTempFileName();
        model.Save(file);
        var back = RoundModel.Load(file);
        Assert.Equal(model.Profile, back.Profile);
        Assert.Equal(model.ReferencePose, back.ReferencePose);
    }
}

public class DiffModelTests
{
    private static VisionImage Plate(int seed, Action<Mat>? defect = null)
    {
        var rnd = new Random(seed);
        using var m = new Mat(60, 80, MatType.CV_8UC3, new Scalar(120, 120, 120));
        using var noise = new Mat(60, 80, MatType.CV_8UC3);
        Cv2.Randu(noise, Scalar.All(0), Scalar.All(10 + rnd.Next(3)));
        Cv2.Add(m, noise, m);
        defect?.Invoke(m);
        return VisionImage.From(m);
    }

    [Fact]
    public void Finds_a_stain_and_passes_good_parts()
    {
        var goods = Enumerable.Range(0, 20).Select(i => Plate(i)).ToList();
        var model = DiffModel.Train(goods, color: true);
        goods.ForEach(g => g.Dispose());

        using var good = Plate(99);
        Assert.Equal(0, model.Compare(good).MaxArea);

        using var dark = Plate(100, m => Cv2.Circle(m, new Point(40, 30), 5, new Scalar(40, 40, 40), -1));
        var r = model.Compare(dark);
        Assert.InRange(r.MaxArea, 50, 120);
        Assert.InRange(r.Regions[0].Center.X, 37, 43);

        using var blue = Plate(101, m => Cv2.Circle(m, new Point(20, 20), 4, new Scalar(160, 110, 90), -1));   // 밝기는 비슷, 색만 다름
        Assert.True(model.Compare(blue).MaxArea > 20);
    }

    [Fact]
    public void Save_load_and_setup_copy_share_the_model()
    {
        var goods = Enumerable.Range(0, 5).Select(i => Plate(i)).ToList();
        var model = DiffModel.Train(goods);
        goods.ForEach(g => g.Dispose());
        var dir = Directory.CreateTempSubdirectory().FullName;
        model.Save(dir, "front/diff");

        var loaded = new DiffModel { Path = "front/diff" };
        loaded.Load(dir);
        Assert.Equal(80, loaded.Width);
        using var img = Plate(7);
        Assert.Equal(model.Compare(img).MaxArea, loaded.Compare(img).MaxArea);

        var setup = new Holder { Diff = model };
        var copy = VisionSetup.Copy(setup);
        Assert.True(copy.Diff.IsReady);
        Assert.Equal("front/diff", copy.Diff.Path);
    }

    [Fact]
    public void Size_mismatch_is_explained()
    {
        var model = DiffModel.Train([Plate(1), Plate(2)]);
        using var m = new Mat(10, 10, MatType.CV_8UC1);
        using var small = VisionImage.From(m);
        var ex = Assert.Throws<ArgumentException>(() => model.Compare(small));
        Assert.Contains("same ROI", ex.Message);
    }

    [Fact]
    public void Dispose_releases_the_model()
    {
        var model = DiffModel.Train([Plate(1), Plate(2)]);
        Assert.IsAssignableFrom<IDisposable>(model);
        using var img = Plate(3);

        ((IDisposable)model).Dispose();
        ((IDisposable)model).Dispose();   // 두 번 불러도 된다

        Assert.False(model.IsReady);
        Assert.Throws<InvalidOperationException>(() => model.Compare(img));
    }

    public sealed class Holder { public DiffModel Diff { get; set; } = new(); }
}
