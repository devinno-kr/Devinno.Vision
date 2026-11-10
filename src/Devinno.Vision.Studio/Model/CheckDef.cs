using System.Text.Json.Serialization;

namespace Devinno.Vision.Studio.Model;

/// <summary>검사 항목 하나의 정의 (데이터). 실행할 때 <see cref="IVisionStep"/> 으로 바뀐다.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(BlobAlignCheck), BlobAlignCheck.KindName)]
[JsonDerivedType(typeof(BlobCountCheck), BlobCountCheck.KindName)]
[JsonDerivedType(typeof(PixelCountCheck), PixelCountCheck.KindName)]
[JsonDerivedType(typeof(BrightnessCheck), BrightnessCheck.KindName)]
[JsonDerivedType(typeof(LengthCheck), LengthCheck.KindName)]
public abstract class CheckDef
{
    /// <summary>JSON 의 Kind 값 전부 (등록 순서).</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        [BlobAlignCheck.KindName, BlobCountCheck.KindName, PixelCountCheck.KindName, BrightnessCheck.KindName, LengthCheck.KindName];

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "";

    /// <summary>기준 이미지 좌표의 ROI. 정렬 검사는 비어 있어도 된다 (전체 이미지).</summary>
    public VisionRoi Roi { get; set; }

    /// <summary>JSON 의 Kind 값 (클래스 이름에서 "Check" 를 뺀 것).</summary>
    [JsonIgnore]
    public string Kind => GetType().Name[..^"Check".Length];

    /// <summary>화면 목록에 보이는 종류 이름.</summary>
    [JsonIgnore]
    public string KindLabel => Label;

    // 재정의한 속성에는 [JsonIgnore] 가 따라오지 않으므로 protected 로 둔다
    private protected abstract string Label { get; }

    /// <summary>Kind 이름으로 빈 검사를 만든다. 모르는 이름이면 null.</summary>
    public static CheckDef? New(string kind) => kind switch
    {
        BlobAlignCheck.KindName => new BlobAlignCheck(),
        BlobCountCheck.KindName => new BlobCountCheck(),
        PixelCountCheck.KindName => new PixelCountCheck(),
        BrightnessCheck.KindName => new BrightnessCheck(),
        LengthCheck.KindName => new LengthCheck(),
        _ => null,
    };
}
