using System.Text.Json;

namespace Devinno.Vision;

/// <summary>
/// A defect classifier: an ONNX model that sorts an image into good and defect kinds (trained from labelled good and defect images).
/// Used to name the defect, or to judge when the defects are known in advance.
/// </summary>
/// <remarks>
/// The model folder holds <c>model.onnx</c> (input [1, 3, H, W], output [1, N] scores, one per label) and <c>model.json</c>:
/// <code>{ "Labels": ["good", "scratch", …], "GoodLabel": "good", "InputWidth": 224, "InputHeight": 224, "Normalize": "ImageNet" }</code>
/// <c>Normalize</c> is <c>ImageNet</c> (default), <c>Unit</c> or <c>Gray</c> (<see cref="VisionTensorFormat"/>). Studio's training writes this folder.
/// <para>Training needs real, labelled defect images; the library only runs the model. In a recipe the ONNX check uses it.
/// The exception messages of this class are Korean.</para>
/// </remarks>
/// <example>
/// <code>
/// using var model = DefectModel.Load(@"C:\recipes\nut\models\defect-1");
///
/// job.Add("defect", roi: () =&gt; roi, ctx =&gt;
/// {
///     DefectResult d = model.Classify(ctx.Image);
///     ctx.DrawText(new Point2d(4, 16), $"{d.Label} {d.Probability:0.00}");
///     ctx.Value("defect probability", d.DefectProbability, max: 0.5, code: d.Defect.ToUpperInvariant());
/// });
/// </code>
/// </example>
public sealed class DefectModel : IDisposable
{
    /// <summary>The model file name in the folder.</summary>
    public const string ModelFile = "model.onnx";

    /// <summary>The description file name in the folder.</summary>
    public const string InfoFile = "model.json";

    private readonly OnnxModel _onnx;

    private DefectModel(string folder, OnnxModel onnx, IReadOnlyList<string> labels, string goodLabel)
    {
        Folder = folder;
        _onnx = onnx;
        Labels = labels;
        GoodLabel = goodLabel;
    }

    /// <summary>The model folder.</summary>
    public string Folder { get; }

    /// <summary>The labels, in output order.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>The label of good parts.</summary>
    public string GoodLabel { get; }

    /// <summary>Input width (px).</summary>
    public int InputWidth => _onnx.InputWidth;

    /// <summary>Input height (px).</summary>
    public int InputHeight => _onnx.InputHeight;

    /// <summary>Reads a model folder (model.onnx + model.json).</summary>
    /// <param name="folder">The model folder.</param>
    /// <returns>The model. Dispose it when done.</returns>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">model.onnx or model.json is missing.</exception>
    /// <exception cref="InvalidDataException">model.json is wrong: broken JSON, fewer than two labels or repeated labels, the good label is not among the labels,
    /// an input size that is not positive, or an unknown <c>Normalize</c>.</exception>
    public static DefectModel Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var info = Path.Combine(folder, InfoFile);
        var onnx = Path.Combine(folder, ModelFile);
        if (!File.Exists(info))
            throw new FileNotFoundException($"{InfoFile} 이 없습니다: {folder}", info);
        if (!File.Exists(onnx))
            throw new FileNotFoundException($"{ModelFile} 이 없습니다: {folder}", onnx);
        Info? i;
        try
        {
            i = JsonSerializer.Deserialize<Info>(File.ReadAllText(info));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{InfoFile} 을 읽을 수 없습니다: {ex.Message}", ex);
        }
        if (i?.Labels is not { Length: >= 2 } labels || labels.Any(string.IsNullOrWhiteSpace) || labels.Distinct().Count() != labels.Length)
            throw new InvalidDataException($"{InfoFile} 의 Labels 는 서로 다른 이름 2개 이상이어야 합니다.");
        var good = string.IsNullOrWhiteSpace(i.GoodLabel) ? "good" : i.GoodLabel;
        if (!labels.Contains(good))
            throw new InvalidDataException($"{InfoFile} 의 Labels 에 양품 라벨 '{good}' 이 없습니다.");
        if (i.InputWidth <= 0 || i.InputHeight <= 0)
            throw new InvalidDataException($"{InfoFile} 의 InputWidth · InputHeight 가 잘못됐습니다.");
        var format = (i.Normalize ?? "ImageNet") switch
        {
            "ImageNet" => VisionTensorFormat.ImageNet,
            "Unit" => VisionTensorFormat.Unit,
            "Gray" => VisionTensorFormat.Gray,
            var other => throw new InvalidDataException($"{InfoFile} 의 Normalize 를 모릅니다: {other} (ImageNet · Unit · Gray)"),
        };
        var model = OnnxModel.Load(onnx, i.InputWidth, i.InputHeight, format);
        return new DefectModel(Path.GetFullPath(folder), model, labels, good);
    }

    /// <summary>Classifies an image.</summary>
    /// <param name="image">The image (resized to the model input).</param>
    /// <returns>The most likely label and the probability of each label.</returns>
    /// <exception cref="InvalidDataException">The model output does not have one score per label.</exception>
    public DefectResult Classify(VisionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var t = _onnx.Run(image).Ordered[0];
        if (t.Data.Length != Labels.Count)
            throw new InvalidDataException($"불량 모델 출력이 {t.Data.Length}개인데 라벨은 {Labels.Count}개입니다: {Folder}");
        // 점수 → 확률 (softmax)
        var max = t.Data.Max();
        var e = t.Data.Select(v => Math.Exp(v - max)).ToArray();
        var sum = e.Sum();
        var p = new Dictionary<string, double>();
        for (var k = 0; k < e.Length; k++)
            p[Labels[k]] = e[k] / sum;
        var best = p.MaxBy(kv => kv.Value);
        var defect = p.Where(kv => kv.Key != GoodLabel).MaxBy(kv => kv.Value);
        return new DefectResult(best.Key, best.Value, defect.Key, 1 - p[GoodLabel], p);
    }

    /// <summary>Releases the model.</summary>
    public void Dispose() => _onnx.Dispose();

    private sealed record Info(string[]? Labels, string? GoodLabel, int InputWidth, int InputHeight, string? Normalize);
}

/// <summary>Result of <see cref="DefectModel.Classify"/>.</summary>
/// <param name="Label">The most likely label.</param>
/// <param name="Probability">Its probability (0–1).</param>
/// <param name="Defect">The most likely defect label (never the good label).</param>
/// <param name="DefectProbability">The probability that the part is not good (1 − probability of the good label).</param>
/// <param name="Probabilities">The probability of each label.</param>
public sealed record DefectResult(string Label, double Probability, string Defect, double DefectProbability, IReadOnlyDictionary<string, double> Probabilities);
