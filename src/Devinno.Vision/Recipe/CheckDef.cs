using System.Text.Json.Serialization;

namespace Devinno.Vision.Recipes;

/// <summary>The definition of one check in a recipe (data). When a job is made it becomes an <see cref="IVisionStep"/>.</summary>
/// <remarks>The kind names and property names are the recipe file format (studio.json); they do not change once published.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(BlobAlignCheck), BlobAlignCheck.KindName)]
[JsonDerivedType(typeof(BlobCountCheck), BlobCountCheck.KindName)]
[JsonDerivedType(typeof(PixelCountCheck), PixelCountCheck.KindName)]
[JsonDerivedType(typeof(BrightnessCheck), BrightnessCheck.KindName)]
[JsonDerivedType(typeof(LengthCheck), LengthCheck.KindName)]
public abstract class CheckDef
{
    /// <summary>All <c>Kind</c> values of the file format, in registration order.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        [BlobAlignCheck.KindName, BlobCountCheck.KindName, PixelCountCheck.KindName, BrightnessCheck.KindName, LengthCheck.KindName];

    /// <summary>The id of this check, unique within a recipe.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The check name, unique within a recipe. It is also the step name in the job (<c>r.Steps[name]</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>The ROI in base image coordinates. An alignment check may leave it empty (whole image).</summary>
    public VisionRoi Roi { get; set; }

    /// <summary>The <c>Kind</c> value in JSON (the class name without "Check").</summary>
    [JsonIgnore]
    public string Kind => GetType().Name[..^"Check".Length];

    /// <summary>The kind name shown in a list on screen.</summary>
    [JsonIgnore]
    public string KindLabel => Label;

    // 재정의한 속성에는 [JsonIgnore] 가 따라오지 않으므로 protected 로 둔다
    private protected abstract string Label { get; }

    /// <summary>Makes the step that runs this check. The current values are copied in, so editing the definition afterwards does not change the step.</summary>
    /// <param name="recipe">The recipe this check belongs to (for example, calibration decides mm or px).</param>
    /// <returns>A step named <see cref="Name"/>. <see cref="VisionRecipe.CreateJob"/> adds it with <see cref="Roi"/>.</returns>
    public abstract IVisionStep CreateStep(VisionRecipe recipe);

    /// <summary>Checks that this definition can run.</summary>
    /// <returns>What is wrong, or <see langword="null"/> when nothing is.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "검사 이름이 비었습니다";
        return this switch
        {
            BlobAlignCheck { Blob: null } or BlobCountCheck { Blob: null } => $"{Name}: Blob 설정이 없습니다",
            BlobCountCheck { Code: null } or PixelCountCheck { Code: null } or BrightnessCheck { Code: null } or LengthCheck { Code: null } => $"{Name}: Code 가 없습니다",
            _ => null,
        };
    }

    /// <summary>이 검사가 부품 위치(Pose)를 만드는가.</summary>
    internal virtual bool MakesPose => false;

    /// <summary>Makes an empty check of a kind.</summary>
    /// <param name="kind">One of <see cref="Kinds"/>.</param>
    /// <returns>A new check, or <see langword="null"/> for an unknown kind.</returns>
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
