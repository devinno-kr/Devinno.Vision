using System.Text.Json;
using Devinno.Vision.Recipes;

namespace Devinno.Vision.Studio.Session;

/// <summary>양품 모델 학습 결과 요약 (모델 폴더의 train.json — 엔지니어 PC 경로가 들어 있어 패키지에는 넣지 않는다).</summary>
/// <param name="Folder">학습한 양품 폴더.</param>
/// <param name="Images">학습에 넘긴 이미지 수.</param>
/// <param name="Learned">모델이 기억한 이미지 수.</param>
/// <param name="Calibration">문턱을 정한 이미지 수.</param>
/// <param name="Max">문턱.</param>
/// <param name="MsPerImage">이 PC 에서 장당 시간 (ms).</param>
/// <param name="Skipped">뺀 이미지 (파일 이름: 이유).</param>
/// <param name="Trained">학습한 시각.</param>
public sealed record GoodTrainingSummary(string Folder, int Images, int Learned, int Calibration, double Max, double MsPerImage, IReadOnlyList<string> Skipped, DateTime Trained);

// 양품 모델 기준 — 양품 폴더 · 영역 · 학습 · 취소
public sealed partial class StudioSession
{
    internal static string Training => Texts.T("good.training");
    internal const string GoodSetFile = "goodset.json";
    internal const string TrainFile = "train.json";

    /// <summary>양품이 이보다 적으면 경고한다 (실험: 40장으로 학습하면 양품 22장 중 6장 오판, 200장이면 1장).</summary>
    internal const int RecommendedGoods = 100;

    private static readonly string[] ImageExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];

    private GoodSet _goods = new();
    private VisionRoi _goodRoi;
    private CancellationTokenSource? _training;
    private GoodModelProgress? _trainProgress;

    /// <summary>특징 모델 (Studio 실행 폴더의 models/features.onnx — tools/export_features.py 로 만든다).</summary>
    internal string FeaturesPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "models", "features.onnx");

    /// <summary>특징 모델 입력 크기 (px). 테스트의 작은 모델은 64.</summary>
    internal int FeatureInputSize { get; set; } = 256;

    /// <summary>양품 학습 입력 (프로젝트 폴더의 goodset.json — Studio 만 쓴다).</summary>
    private sealed class GoodSet
    {
        public string? Folder { get; set; }
        public List<string> Extra { get; set; } = [];

        /// <summary>ONNX 검사 id → 불량 폴더.</summary>
        public Dictionary<string, string> Defects { get; set; } = [];
    }

    // ── 상태 (화면 · MCP 가 읽는다) ───────────────────────

    /// <summary>양품 모델을 학습하는 중인가.</summary>
    public bool IsTraining { get { lock (_gate) return _training is not null; } }

    /// <summary>학습 진행 (학습 중이 아니면 null).</summary>
    public GoodModelProgress? TrainingProgress { get { lock (_gate) return _trainProgress; } }

    /// <summary>양품 폴더 (없으면 null).</summary>
    public string? GoodFolder { get { lock (_gate) return _goods.Folder; } }

    /// <summary>양품 모델이 볼 영역 (빈 값 = 전체 이미지).</summary>
    public VisionRoi GoodRoi { get { lock (_gate) return _goodRoi; } }

    /// <summary>학습에 넘길 양품 이미지 (폴더의 이미지 + 테스트에서 더한 것).</summary>
    public IReadOnlyList<string> GoodImages() { lock (_gate) return GoodImagesCore(); }

    /// <summary>지금 양품 모델의 학습 요약. 모델이 없거나 요약 파일이 없으면 null.</summary>
    public GoodTrainingSummary? GoodSummary
    {
        get
        {
            string? file;
            lock (_gate)
                file = _project is { GoodModel: { } g } p ? Path.Combine(p.Folder, g.Path, TrainFile) : null;
            if (file is null || !File.Exists(file))
                return null;
            try
            {
                return JsonSerializer.Deserialize<GoodTrainingSummary>(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>기준 이미지가 없을 때 화면 바탕으로 쓸 양품 이미지 (첫 장). 없으면 null.</summary>
    public string? GoodPreviewFile { get { lock (_gate) return GoodImagesCore().FirstOrDefault(); } }

    // ── 명령 ─────────────────────────────────────────────

    /// <summary>양품 이미지 폴더를 정한다 (폴더 바로 아래의 이미지가 모두 양품).</summary>
    public CommandResult SetGoodFolder(string folder) => Change(() =>
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return CommandResult.Fail(Texts.T("good.noFolder", folder));
        var full = Path.GetFullPath(folder);
        if (NotAnImageFolder(full, Texts.T("good.goodFolder")) is { } refuse)
            return CommandResult.Fail(refuse);
        var count = ImagesIn(full).Count;
        if (count == 0)
            return CommandResult.Fail(Texts.T("good.noImages", full));
        _goods.Folder = full;
        if (SaveGoodSet() is { } error)
            return CommandResult.Fail(error);
        var warnings = string.Concat(_project!.TestSets.Select(t => OverlapWarning(full, t.Folder, Texts.T("good.testFolder"))));
        return CommandResult.Done(Texts.T("good.folderSet", full, count) + FewWarning(count + _goods.Extra.Count) + warnings);
    }, markDirty: false);

    /// <summary>양품 이미지 한 장을 더한다 (테스트에서 양품인데 NG 로 나온 이미지). 다시 학습해야 반영된다.</summary>
    public CommandResult AddGoodImage(string file) => Change(() =>
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return CommandResult.Fail(Texts.T("good.noImageFile", file));
        var full = Path.GetFullPath(file);
        if (GoodImagesCore().Contains(full, StringComparer.OrdinalIgnoreCase))
            return CommandResult.Fail(Texts.T("good.alreadyGood", Path.GetFileName(full)));
        _goods.Extra.Add(full);
        if (SaveGoodSet() is { } error)
        {
            _goods.Extra.Remove(full);
            return CommandResult.Fail(error);
        }
        return CommandResult.Done(Texts.T("good.imageAdded", Path.GetFileName(full), GoodImagesCore().Count));
    }, markDirty: false);

    /// <summary>양품 모델이 볼 영역. 빈 값이면 전체 이미지. 학습한 모델이 있으면 다시 학습해야 한다.</summary>
    public CommandResult SetGoodRoi(VisionRoi roi) => Change(() =>
    {
        _goodRoi = roi.IsEmpty ? default : roi;
        if (_project!.GoodModel is { } g && !roi.IsEmpty)
            g.Roi = roi;
        var stale = _project.GoodModel is not null && !_project.GoodModelIsCurrent ? Texts.T("good.retrain") : "";
        return CommandResult.Done(Texts.T("good.roiSet", Describe(_goodRoi)) + stale);
    });

    /// <summary>양품 모델을 뺀다 (저장할 때 모델 폴더도 지운다). 사유 검사는 그대로 남는다.</summary>
    public CommandResult RemoveGoodModel() => Change(() =>
    {
        var p = _project!;
        if (p.GoodModel is null)
            return CommandResult.Fail(Texts.T("good.noModel"));
        p.GoodModel = null;
        var reasons = p.Checks.Count(c => c.Role == StepRole.Reason);
        return CommandResult.Done(Texts.T("good.modelRemoved") + (reasons > 0 ? Texts.T("good.reasonsNotJudged", reasons) : ""));
    });

    /// <summary>양품 폴더로 양품 모델을 학습한다 (오래 걸린다 — 진행률, <see cref="CancelTraining"/>). 학습 중에는 다른 명령을 막는다.</summary>
    public async Task<CommandResult> TrainGoodModelAsync(IProgress<GoodModelProgress>? progress = null)
    {
        VisionRecipe p;
        IReadOnlyList<string> images;
        VisionRoi roi;
        string? folder;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_training is not null)
                return CommandResult.Fail(Texts.T("good.alreadyTraining"));
            if (_project is not { } cur)
                return CommandResult.Fail(NoProject);
            images = GoodImagesCore();
            if (images.Count == 0)
                return CommandResult.Fail(Texts.T("good.folderFirst"));
            if (!File.Exists(FeaturesPath))
                return CommandResult.Fail(Texts.T("good.noFeatures", FeaturesPath));
            p = cur;
            roi = _goodRoi;
            folder = _goods.Folder;
            _training = cts = new CancellationTokenSource();
            _trainProgress = null;
        }
        ProjectChanged?.Invoke();
        try
        {
            var relay = new Relay<GoodModelProgress>(x =>
            {
                lock (_gate)
                    _trainProgress = x;
                progress?.Report(x);
                ProjectChanged?.Invoke();
            });
            var training = new GoodModelTraining { Images = images, Features = FeaturesPath, Roi = roi, InputSize = FeatureInputSize };
            var r = await Task.Run(() => p.TrainGoodModelAsync(training, relay, cts.Token)).ConfigureAwait(false);
            var summary = new GoodTrainingSummary(folder ?? "", images.Count, r.Learned, r.CalibrationScores.Count, r.Model.Max, Math.Round(r.MsPerImage, 1),
                                                  [.. r.Skipped.Select(s => $"{Path.GetFileName(s.File)}: {s.Reason}")], DateTime.Now);
            try
            {
                File.WriteAllText(Path.Combine(p.Folder, r.Model.Path, TrainFile), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 요약을 못 써도 모델은 쓸 수 있다
            }
            lock (_gate)
            {
                _goodRoi = r.Model.Roi;
                _revision++;
                _dirty = true;
            }
            var skipped = r.Skipped.Count > 0 ? Texts.T("good.skipped", r.Skipped.Count, string.Join(", ", r.Skipped.Take(3).Select(s => Path.GetFileName(s.File)))) : "";
            // 이전 샘플(18장)로 잰 결과: 전체 이미지(640×480 → 256)로는 결함 6장을 놓쳤고, 부품만 감싸면 18/18
            var whole = roi.IsEmpty ? Texts.T("good.wholeImage") : "";
            return CommandResult.Done(Texts.T("good.trained", r.Learned, r.CalibrationScores.Count, r.Model.Max, r.MsPerImage) + skipped + FewWarning(images.Count) + whole);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail(Texts.T("good.canceled"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CommandResult.Fail(Texts.T("good.trainFailed", ex.Message));
        }
        finally
        {
            lock (_gate)
            {
                _training = null;
                _trainProgress = null;
            }
            cts.Dispose();
            ProjectChanged?.Invoke();
        }
    }

    /// <summary>학습을 취소한다.</summary>
    public CommandResult CancelTraining()
    {
        lock (_gate)
        {
            if (_training is not { } cts)
                return CommandResult.Fail(Texts.T("good.notTraining"));
            cts.Cancel();
        }
        return CommandResult.Done(Texts.T("good.canceling"));
    }

    // ── 내부 ─────────────────────────────────────────────

    // 잠금 안에서: 프로젝트를 바꾼다 (옛 레시피의 모델을 닫고, 양품 입력을 읽는다)
    private void Replace(VisionRecipe p)
    {
        if (!ReferenceEquals(_project, p))
            _project?.Dispose();
        _project = p;
        _lastReport = null;
        _dirty = false;
        _goodRoi = p.GoodModel?.Roi ?? default;
        _goods = LoadGoodSet(p.Folder);
    }

    private static GoodSet LoadGoodSet(string folder)
    {
        var file = Path.Combine(folder, GoodSetFile);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<GoodSet>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    // 잠금 안에서. 실패하면 이유
    private string? SaveGoodSet()
    {
        try
        {
            File.WriteAllText(Path.Combine(_project!.Folder, GoodSetFile), JsonSerializer.Serialize(_goods, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Texts.T("good.saveListFailed", ex.Message);
        }
    }

    // 잠금 안에서
    private List<string> GoodImagesCore()
    {
        var list = _goods.Folder is { } f && Directory.Exists(f) ? ImagesIn(f) : [];
        if (_project?.BaseImagePath is { } b)
            list.RemoveAll(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase));   // 기준 이미지 사본
        foreach (var e in _goods.Extra)
            if (File.Exists(e) && !list.Contains(e, StringComparer.OrdinalIgnoreCase))
                list.Add(e);
        return list;
    }

    private static List<string> ImagesIn(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string FewWarning(int count) => count < RecommendedGoods
        ? Texts.T("good.fewWarning", count, RecommendedGoods)
        : "";

    private static string OverlapWarning(string folder, string? other, string what) => other is not null && Overlaps(folder, other)
        ? Texts.T("good.overlapWarning", what, other)
        : "";

    // 잠금 안에서: Job 을 만든다. 못 만들면 이유 (모델 없음 · 다시 학습 필요 등)
    private static string? TryCreateJob(VisionRecipe p, out VisionJob job)
    {
        job = null!;
        if (p.Checks.Count == 0 && p.GoodModel is null)
            return Texts.T("good.noChecks");
        try
        {
            job = p.CreateJob();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return ex.Message;
        }
    }

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
