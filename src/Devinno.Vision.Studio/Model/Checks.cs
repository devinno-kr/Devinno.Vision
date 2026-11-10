namespace Devinno.Vision.Studio.Model;

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

/// <summary>가장 큰 블롭으로 부품 위치(Pose)를 잡는다. 뒤 검사들의 ROI 가 따라간다.</summary>
public sealed class BlobAlignCheck : CheckDef
{
    public const string KindName = "BlobAlign";
    private protected override string Label => "블롭 정렬";

    public BlobParams Blob { get; set; } = new();

    /// <summary>기준 이미지에서 티칭한 부품 위치. 없으면 정렬하지 않는다.</summary>
    public VisionPose? Reference { get; set; }

    internal override bool MakesPose => true;

    internal override IVisionStep Build(StudioProject p)
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

/// <summary>ROI 안 블롭 개수를 범위로 판정.</summary>
public sealed class BlobCountCheck : CheckDef
{
    public const string KindName = "BlobCount";
    private protected override string Label => "블롭 개수";

    public BlobParams Blob { get; set; } = new();
    public int Min { get; set; }
    public int Max { get; set; } = 1;
    public string Code { get; set; } = "COUNT";

    internal override IVisionStep Build(StudioProject p)
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

/// <summary>ROI 안 밝기 Low~High 픽셀 수가 Max 이하인지.</summary>
public sealed class PixelCountCheck : CheckDef
{
    public const string KindName = "PixelCount";
    private protected override string Label => "픽셀 수";

    public int Low { get; set; }
    public int High { get; set; } = 100;
    public int Max { get; set; }
    public string Code { get; set; } = "PIXEL";

    internal override IVisionStep Build(StudioProject p)
    {
        var (name, low, high, max, code) = (Name, Low, High, Max, Code);
        return new CheckStep(name, ctx => ctx.Value($"{name} 픽셀", Brightness.Count(ctx.Image, low, high), max: max, code: code));
    }
}

/// <summary>ROI 평균 밝기를 범위로 판정.</summary>
public sealed class BrightnessCheck : CheckDef
{
    public const string KindName = "Brightness";
    private protected override string Label => "평균 밝기";

    public double Min { get; set; }
    public double Max { get; set; } = 255;
    public string Code { get; set; } = "BRIGHT";

    internal override IVisionStep Build(StudioProject p)
    {
        var (name, min, max, code) = (Name, Min, Max, Code);
        return new CheckStep(name, ctx => ctx.Value($"{name} 밝기", Brightness.Mean(ctx.Image), min, max, code));
    }
}

/// <summary>ROI 가로 방향 두 에지 사이 거리를 공칭값 ± 공차로 판정 (캘리브레이션 있으면 mm, 없으면 px).</summary>
public sealed class LengthCheck : CheckDef
{
    public const string KindName = "Length";
    private protected override string Label => "폭/길이";

    public EdgePolarity Polarity { get; set; } = EdgePolarity.BrightObject;
    public double MinContrast { get; set; } = 8;
    public double Nominal { get; set; }
    public double Plus { get; set; }
    public double Minus { get; set; }
    public string Code { get; set; } = "LENGTH";

    internal override IVisionStep Build(StudioProject p)
    {
        var (name, polarity, contrast, nominal, plus, minus, code) = (Name, Polarity, MinContrast, Nominal, Plus, Minus, Code);
        var mm = p.Calibration is not null;
        return new CheckStep(name, ctx =>
        {
            var e = Edge.Caliper(ctx.Image, polarity, contrast);
            ctx.Draw(e);
            ctx.Value(name, mm ? ctx.Mm(e) : e.Distance, nominal: nominal, plus: plus, minus: minus, code: code, unit: mm ? "mm" : "px");
        });
    }
}
