namespace Devinno.Vision.Studio.Model;

/// <summary>가장 큰 블롭으로 부품 위치(Pose)를 잡는다. 뒤 검사들의 ROI 가 따라간다.</summary>
public sealed class BlobAlignCheck : CheckDef
{
    public const string KindName = "BlobAlign";
    private protected override string Label => "블롭 정렬";

    public BlobParams Blob { get; set; } = new();

    /// <summary>기준 이미지에서 티칭한 부품 위치. 없으면 정렬하지 않는다.</summary>
    public VisionPose? Reference { get; set; }
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
}

/// <summary>ROI 평균 밝기를 범위로 판정.</summary>
public sealed class BrightnessCheck : CheckDef
{
    public const string KindName = "Brightness";
    private protected override string Label => "평균 밝기";

    public double Min { get; set; }
    public double Max { get; set; } = 255;
    public string Code { get; set; } = "BRIGHT";
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
}
