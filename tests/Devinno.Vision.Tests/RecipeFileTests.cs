using Devinno.Vision;
using Devinno.Vision.Recipes;

public class RecipeFileTests
{
    private static VisionRecipe AllKinds(string folder)
    {
        var p = VisionRecipe.Create(folder, "플레이트");
        p.BaseImage = "images/base.png";
        p.Calibration = VisionCalibration.Scale(0.1);
        p.Checks.Add(new BlobAlignCheck { Name = "정렬", Blob = new BlobParams { MinArea = 20000 }, Reference = new VisionPose(330.4, 256, -0.07) });
        p.Checks.Add(new BlobCountCheck { Name = "왼쪽 구멍", Roi = VisionRoi.Rect(138, 130, 60, 60), Blob = new BlobParams { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 }, Min = 1, Max = 1, Code = "HOLE" });
        p.Checks.Add(new PixelCountCheck { Name = "노치", Roi = VisionRoi.Rect(238, 116, 36, 20), Low = 0, High = 100, Max = 200, Code = "NOTCH" });
        p.Checks.Add(new BrightnessCheck { Name = "밝기", Roi = VisionRoi.Rotated(100, 100, 40, 20, 15), Min = 50, Max = 200 });
        p.Checks.Add(new LengthCheck { Name = "길이", Roi = VisionRoi.Rect(100, 180, 440, 12), Polarity = EdgePolarity.DarkObject, MinContrast = 9, Nominal = 41, Plus = 0.7, Minus = 0.5 });
        p.TestSets.Add(new TestSet { Folder = "C:/samples", Rule = LabelRule.FileName });
        return p;
    }

    [Fact]
    public void Save_load_round_trips_all_kinds()
    {
        using var dir = new TempDir();
        var saved = AllKinds(dir.Path);
        saved.Save();

        var p = VisionRecipe.Load(dir.Path);

        Assert.Equal(dir.Path, p.Folder);
        Assert.Equal("플레이트", p.Name);
        Assert.Equal(1, p.FormatVersion);
        Assert.Equal("images/base.png", p.BaseImage);
        Assert.Equal(0.1, p.Calibration!.MmPerPx);
        Assert.Equal(5, p.Checks.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(saved.Checks[i].GetType(), p.Checks[i].GetType());
            Assert.Equal(saved.Checks[i].Id, p.Checks[i].Id);
            Assert.Equal(saved.Checks[i].Name, p.Checks[i].Name);
            Assert.Equal(saved.Checks[i].Roi, p.Checks[i].Roi);
        }
        var align = Assert.IsType<BlobAlignCheck>(p.Checks[0]);
        Assert.Equal(20000, align.Blob.MinArea);
        Assert.Equal(new VisionPose(330.4, 256, -0.07), align.Reference);
        Assert.True(align.Roi.IsEmpty);
        var count = Assert.IsType<BlobCountCheck>(p.Checks[1]);
        Assert.Equal((ThresholdMode.Fixed, 80, true, 300), (count.Blob.Mode, count.Blob.Threshold, count.Blob.Invert, count.Blob.MinArea));
        Assert.Equal((1, 1, "HOLE"), (count.Min, count.Max, count.Code));
        var pixel = Assert.IsType<PixelCountCheck>(p.Checks[2]);
        Assert.Equal((0, 100, 200, "NOTCH"), (pixel.Low, pixel.High, pixel.Max, pixel.Code));
        var bright = Assert.IsType<BrightnessCheck>(p.Checks[3]);
        Assert.Equal((50.0, 200.0, "BRIGHT"), (bright.Min, bright.Max, bright.Code));
        var len = Assert.IsType<LengthCheck>(p.Checks[4]);
        Assert.Equal((EdgePolarity.DarkObject, 9.0, 41.0, 0.7, 0.5, "LENGTH"), (len.Polarity, len.MinContrast, len.Nominal, len.Plus, len.Minus, len.Code));
        var set = Assert.Single(p.TestSets);
        Assert.Equal(("C:/samples", LabelRule.FileName, "good"), (set.Folder, set.Rule, set.GoodLabel));
    }

    [Fact]
    public void Unknown_kind_fails_with_clear_message()
    {
        using var dir = new TempDir();
        AllKinds(dir.Path).Save();
        var file = Path.Combine(dir.Path, VisionRecipe.FileName);
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Kind\": \"BlobCount\"", "\"Kind\": \"Hologram\""));

        var ex = Assert.Throws<InvalidDataException>(() => VisionRecipe.Load(dir.Path));

        Assert.Contains("Hologram", ex.Message);
        Assert.Contains("알 수 없는 검사 종류", ex.Message);
    }

    [Fact]
    public void Json_is_readable()
    {
        using var dir = new TempDir();
        AllKinds(dir.Path).Save();

        var text = File.ReadAllText(Path.Combine(dir.Path, VisionRecipe.FileName));

        Assert.Contains("\"Kind\": \"BlobCount\"", text);
        Assert.Contains("\"Shape\": \"Rect\"", text);
        Assert.Contains("왼쪽 구멍", text);   // 한글이 \uXXXX 로 바뀌지 않게
    }
}
