using Devinno.Vision;
using Devinno.Vision.Recipes;
using OpenCvSharp;

public class RecipeCheckTests
{
    private static async Task<VisionResult> Run(VisionRecipe p, string sample)
    {
        using var img = VisionImage.Load(BracketInspection.Sample(sample));
        return await p.CreateJob().RunAsync(img);
    }

    [Fact]
    public async Task BlobCount_counts_the_left_hole()
    {
        var p = VisionRecipe.Create(Path.GetTempPath(), "t");
        p.Checks.Add(new BlobCountCheck
        {
            Name = "왼쪽 구멍",
            Roi = VisionRoi.Rect(194, 251, 60, 60),
            Blob = new BlobParams { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 },
            Min = 1,
            Max = 1,
        });

        using var good = await Run(p, "01_good.bmp");
        using var bad = await Run(p, "02_missinghole.bmp");

        Assert.Equal(VisionState.Ok, good.State);
        Assert.Equal(1, Assert.Single(good.Values).Value);
        Assert.Equal("왼쪽 구멍 개수", good.Values[0].Name);
        Assert.Equal(VisionState.Ng, bad.State);
        Assert.Equal(["COUNT"], bad.Codes);
    }

    [Fact]
    public async Task Length_uses_px_without_calibration_and_mm_with_it()
    {
        var p = VisionRecipe.Create(Path.GetTempPath(), "t");
        var len = new LengthCheck { Name = "길이", Roi = VisionRoi.Rect(100, 305, 470, 30), MinContrast = 8, Nominal = 410, Plus = 7, Minus = 5 };
        p.Checks.Add(len);

        using (var px = await Run(p, "01_good.bmp"))
        {
            Assert.Equal(VisionState.Ok, px.State);
            Assert.Equal("px", Assert.Single(px.Values).Unit);
        }

        p.Calibration = VisionCalibration.Scale(0.1);
        len.Nominal = 41;
        len.Plus = 0.7;
        len.Minus = 0.5;
        using var mm = await Run(p, "01_good.bmp");

        Assert.Equal(VisionState.Ok, mm.State);
        var v = Assert.Single(mm.Values);
        Assert.Equal("mm", v.Unit);
        Assert.Equal(41.08, v.Value, 0.1);
        Assert.Equal(41, v.Nominal);
    }

    [Fact]
    public async Task Align_without_blob_fails_the_step()
    {
        var p = VisionRecipe.Create(Path.GetTempPath(), "t");
        p.Checks.Add(new BlobAlignCheck { Name = "정렬", Blob = new BlobParams { MinArea = 20000 } });
        p.Checks.Add(new BrightnessCheck { Name = "밝기", Roi = VisionRoi.Rect(10, 10, 20, 20) });
        using var mat = new Mat(480, 640, MatType.CV_8UC1, Scalar.Black);
        using var black = VisionImage.From(mat);

        using var r = await p.CreateJob().RunAsync(black);

        Assert.Equal(VisionState.Error, r.State);
        Assert.Equal(StepState.Error, r.Steps["정렬"].State);
        Assert.Equal("부품 없음", r.Steps["정렬"].Reason);
    }
}
