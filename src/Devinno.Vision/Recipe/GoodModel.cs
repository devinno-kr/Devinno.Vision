namespace Devinno.Vision.Recipes;

/// <summary>
/// The good-part model of a recipe: a reference learned from good images (<see cref="AnomalyModel"/>).
/// It judges whether the part looks like the good parts; standard checks can then name the defect (<see cref="StepRole.Reason"/>).
/// </summary>
/// <remarks>
/// Made by <see cref="VisionRecipe.TrainGoodModelAsync"/>. The model files are in <see cref="Path"/> under the recipe folder.
/// In the job it is the step <see cref="VisionRecipe.GoodModelStepName"/>, which runs after the alignment check and records the value "양품 모델 점수" (the anomaly score).
/// </remarks>
public sealed class GoodModel
{
    /// <summary>The model folder, relative to the recipe folder (models/good-…).</summary>
    public string Path { get; set; } = "";

    /// <summary>The region the model looks at, in base image coordinates. It follows the part when the recipe has an alignment check.</summary>
    public VisionRoi Roi { get; set; }

    /// <summary>The largest anomaly score that is OK.</summary>
    public double Max { get; set; }

    /// <summary>The NG code.</summary>
    public string Code { get; set; } = "SURFACE";

    /// <summary>
    /// What the model was trained for: the alignment check (blob settings, reference pose, ROI) and <see cref="Roi"/>.
    /// When these change the crops change, so <see cref="VisionRecipe.CreateJob"/> asks to train again.
    /// </summary>
    public string Basis { get; set; } = "";
}

/// <summary>Settings of <see cref="VisionRecipe.TrainGoodModelAsync"/>.</summary>
public sealed class GoodModelTraining
{
    /// <summary>The good image files.</summary>
    public required IReadOnlyList<string> Images { get; init; }

    /// <summary>The feature model (.onnx, feature maps [1, C, h, w]; <c>tools/export_features.py</c> makes one). It is copied into the model folder.</summary>
    public required string Features { get; init; }

    /// <summary>The region to learn, in base image coordinates. Empty = the whole image.</summary>
    public VisionRoi Roi { get; init; }

    /// <summary>The feature model input size (px, square). Default 256.</summary>
    public int InputSize { get; init; } = 256;

    /// <summary>Patches kept per position. Default 48.</summary>
    public int Representatives { get; init; } = 48;

    /// <summary>Compare with good patches up to this many cells away. Default 1.</summary>
    public int Neighborhood { get; init; } = 1;

    /// <summary>The share of good images kept out of the model to set the limit (0–0.5). Default 0.1.</summary>
    public double CalibrationShare { get; init; } = 0.1;

    /// <summary>The limit is the highest calibration score times this. Default 1.05.</summary>
    public double Margin { get; init; } = 1.05;

    /// <summary>The NG code. Default "SURFACE".</summary>
    public string Code { get; init; } = "SURFACE";
}

/// <summary>Progress of <see cref="VisionRecipe.TrainGoodModelAsync"/>.</summary>
/// <param name="Stage">What is being done: "crop", "learn" or "limit".</param>
/// <param name="Done">Images done in this stage.</param>
/// <param name="Total">Images in this stage.</param>
public sealed record GoodModelProgress(string Stage, int Done, int Total);

/// <summary>Result of <see cref="VisionRecipe.TrainGoodModelAsync"/>.</summary>
/// <param name="Model">The model now set on the recipe (<see cref="VisionRecipe.GoodModel"/>).</param>
/// <param name="Learned">Images the model learned from.</param>
/// <param name="CalibrationScores">Scores of the good images kept out, in file order; the limit comes from them.</param>
/// <param name="Skipped">Images left out, with the reason (for example the part was not found).</param>
/// <param name="MsPerImage">Average time to score one image on this PC (ms).</param>
/// <param name="Elapsed">The whole training time.</param>
public sealed record GoodModelResult(GoodModel Model, int Learned, IReadOnlyList<double> CalibrationScores, IReadOnlyList<(string File, string Reason)> Skipped,
                                     double MsPerImage, TimeSpan Elapsed);

// 레시피가 가진 모델 — 쓰는 중에 닫혀도 쓰던 호출이 끝난 뒤에 해제한다
internal sealed class SharedModel<T>(string path, T model) : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private int _users;
    private bool _closed;

    public string Path { get; } = path;

    public TResult Use<TResult>(Func<T, TResult> use)
    {
        lock (_gate)
        {
            if (_closed)
                throw new InvalidOperationException($"{(model is AnomalyModel ? "양품" : "불량")} 모델이 닫혔습니다 (레시피를 닫았거나 다시 학습했습니다). Job 을 다시 만드세요.");
            _users++;
        }
        try
        {
            return use(model);
        }
        finally
        {
            lock (_gate)
                if (--_users == 0 && _closed)
                    model.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            if (_users == 0)
                model.Dispose();
        }
    }
}

// 양품 모델 단계: 영역의 이상 점수를 문턱으로 판정
internal sealed class GoodModelStep(SharedModel<AnomalyModel> model, double max, string code) : IVisionStep
{
    public string Name => VisionRecipe.GoodModelStepName;

    public Task RunAsync(VisionContext ctx)
    {
        var a = model.Use(m => m.Score(ctx.Image));
        ctx.Draw(a);
        ctx.Value($"{Name} 점수", a.Score, max: max, code: code);
        return Task.CompletedTask;
    }
}
