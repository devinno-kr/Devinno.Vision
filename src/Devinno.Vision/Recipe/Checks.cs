namespace Devinno.Vision.Recipes;

// 검사 정의 → Job 단계 (이름 = 검사 이름)
internal sealed class CheckStep(string name, Action<VisionContext> run) : IVisionStep
{
    public string Name { get; } = name;

    public Task RunAsync(VisionContext ctx)
    {
        run(ctx);
        return Task.CompletedTask;
    }
}

/// <summary>Finds the part position (pose) from the largest blob. The ROIs of the checks after it follow the part.</summary>
public sealed class BlobAlignCheck : CheckDef
{
    /// <summary>The <c>Kind</c> value in JSON.</summary>
    public const string KindName = "BlobAlign";
    private protected override string Label => "블롭 정렬";

    /// <summary>How the part blob is found.</summary>
    public BlobParams Blob { get; set; } = new();

    /// <summary>The part position taught on the base image. Without it the ROIs are not moved.</summary>
    public VisionPose? Reference { get; set; }

    internal override bool MakesPose => true;

    /// <inheritdoc/>
    public override IVisionStep CreateStep(VisionRecipe recipe)
    {
        var blob = Copy(Blob);
        var reference = Reference;
        return new CheckStep(Name, ctx =>
        {
            var part = Devinno.Vision.Blob.Find(ctx.Image, blob).FirstOrDefault();
            if (part is null)
            {
                ctx.Fail("부품 없음");
                return;
            }
            ctx.Draw(part);
            ctx.SetPose(VisionPose.FromBlob(part, reference), reference);
        });
    }

    internal static BlobParams Copy(BlobParams b) => new() { Mode = b.Mode, Threshold = b.Threshold, Invert = b.Invert, MinArea = b.MinArea, MaxArea = b.MaxArea };
}

/// <summary>Judges the number of blobs in the ROI against a range.</summary>
public sealed class BlobCountCheck : CheckDef
{
    /// <summary>The <c>Kind</c> value in JSON.</summary>
    public const string KindName = "BlobCount";
    private protected override string Label => "블롭 개수";

    /// <summary>How the blobs are found.</summary>
    public BlobParams Blob { get; set; } = new();

    /// <summary>The smallest count that is OK.</summary>
    public int Min { get; set; }

    /// <summary>The largest count that is OK.</summary>
    public int Max { get; set; } = 1;

    /// <summary>The NG code.</summary>
    public string Code { get; set; } = "COUNT";

    /// <inheritdoc/>
    public override IVisionStep CreateStep(VisionRecipe recipe)
    {
        var (blob, name, min, max, code) = (BlobAlignCheck.Copy(Blob), Name, Min, Max, Code);
        return new CheckStep(name, ctx =>
        {
            var found = Devinno.Vision.Blob.Find(ctx.Image, blob);
            ctx.Draw(found);
            ctx.Value($"{name} 개수", found.Length, min, max, code);
        });
    }
}

/// <summary>Judges that the number of pixels in the ROI with brightness <see cref="Low"/>–<see cref="High"/> is at most <see cref="Max"/>.</summary>
public sealed class PixelCountCheck : CheckDef
{
    /// <summary>The <c>Kind</c> value in JSON.</summary>
    public const string KindName = "PixelCount";
    private protected override string Label => "픽셀 수";

    /// <summary>The lowest brightness counted (0–255).</summary>
    public int Low { get; set; }

    /// <summary>The highest brightness counted (0–255).</summary>
    public int High { get; set; } = 100;

    /// <summary>The largest pixel count that is OK.</summary>
    public int Max { get; set; }

    /// <summary>The NG code.</summary>
    public string Code { get; set; } = "PIXEL";

    /// <inheritdoc/>
    public override IVisionStep CreateStep(VisionRecipe recipe)
    {
        var (name, low, high, max, code) = (Name, Low, High, Max, Code);
        return new CheckStep(name, ctx => ctx.Value($"{name} 픽셀", Brightness.Count(ctx.Image, low, high), max: max, code: code));
    }
}

/// <summary>Judges the mean brightness of the ROI against a range.</summary>
public sealed class BrightnessCheck : CheckDef
{
    /// <summary>The <c>Kind</c> value in JSON.</summary>
    public const string KindName = "Brightness";
    private protected override string Label => "평균 밝기";

    /// <summary>The lowest mean brightness that is OK.</summary>
    public double Min { get; set; }

    /// <summary>The highest mean brightness that is OK.</summary>
    public double Max { get; set; } = 255;

    /// <summary>The NG code.</summary>
    public string Code { get; set; } = "BRIGHT";

    /// <inheritdoc/>
    public override IVisionStep CreateStep(VisionRecipe recipe)
    {
        var (name, min, max, code) = (Name, Min, Max, Code);
        return new CheckStep(name, ctx => ctx.Value($"{name} 밝기", Brightness.Mean(ctx.Image), min, max, code));
    }
}

/// <summary>
/// Judges the distance between two edges along the ROI's horizontal direction as nominal ± tolerance
/// (mm when the recipe has a calibration, px otherwise).
/// </summary>
public sealed class LengthCheck : CheckDef
{
    /// <summary>The <c>Kind</c> value in JSON.</summary>
    public const string KindName = "Length";
    private protected override string Label => "폭/길이";

    /// <summary>Whether the object is brighter or darker than the background.</summary>
    public EdgePolarity Polarity { get; set; } = EdgePolarity.BrightObject;

    /// <summary>The smallest brightness step that counts as an edge.</summary>
    public double MinContrast { get; set; } = 8;

    /// <summary>The nominal length (mm with a calibration, px without).</summary>
    public double Nominal { get; set; }

    /// <summary>The tolerance above <see cref="Nominal"/>.</summary>
    public double Plus { get; set; }

    /// <summary>The tolerance below <see cref="Nominal"/>.</summary>
    public double Minus { get; set; }

    /// <summary>The NG code.</summary>
    public string Code { get; set; } = "LENGTH";

    /// <inheritdoc/>
    public override IVisionStep CreateStep(VisionRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var (name, polarity, contrast, nominal, plus, minus, code) = (Name, Polarity, MinContrast, Nominal, Plus, Minus, Code);
        var mm = recipe.Calibration is not null;
        return new CheckStep(name, ctx =>
        {
            var e = Edge.Caliper(ctx.Image, polarity, contrast);
            ctx.Draw(e);
            ctx.Value(name, mm ? ctx.Mm(e) : e.Distance, nominal: nominal, plus: plus, minus: minus, code: code, unit: mm ? "mm" : "px");
        });
    }
}

/// <summary>
/// Classifies the ROI with a defect model (<see cref="DefectModel"/>, trained in Studio or imported) and judges by the probability that it is not good.
/// The NG code is the most likely defect label in capitals (scratch → SCRATCH).
/// </summary>
public sealed class OnnxCheck : CheckDef
{
    /// <summary>The <c>Kind</c> value in JSON.</summary>
    public const string KindName = "Onnx";
    private protected override string Label => "ONNX 검사";

    /// <summary>The model folder relative to the recipe folder (models/defect-…), or empty before training.</summary>
    public string Model { get; set; } = "";

    /// <summary>NG when the probability that the part is not good is above this (0–1).</summary>
    public double Threshold { get; set; } = 0.5;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">No model yet.</exception>
    public override IVisionStep CreateStep(VisionRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException($"{Name}: 불량 모델이 없습니다 — [학습] 하거나 ONNX 를 가져오세요.");
        var model = recipe.DefectModelAt(Model);
        var (name, threshold) = (Name, Threshold);
        return new CheckStep(name, ctx =>
        {
            var r = model.Use(m => m.Classify(ctx.Image));
            ctx.DrawText(new OpenCvSharp.Point2d(4, 16), $"{r.Label} {r.Probability:0.00}");
            ctx.Value($"{name} 불량 확률", r.DefectProbability, max: threshold, code: r.Defect.ToUpperInvariant());
        });
    }
}
