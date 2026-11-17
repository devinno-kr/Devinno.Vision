using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Devinno.Vision.Recipes;

namespace Devinno.Vision;

/// <summary>
/// An inspection made in Studio: a folder with <c>studio.json</c> (checks, calibration, taught reference pose, good-part model), the base image and model files.
/// An app loads the folder and runs the same inspection without Studio.
/// </summary>
/// <remarks>
/// <para>The reference is a base image (standard checks), a good-part model (<see cref="GoodModel"/>), or both.</para>
/// <para><b>Use</b>: load the recipe once (<see cref="Load"/>), make the job once (<see cref="CreateJob"/>) and run that job for every image.
/// Loading reads the ONNX model files, so do not load per image.</para>
/// <para><b>Lifetime</b>: a recipe holds the loaded models. Keep it alive as long as its jobs are used and dispose it afterwards;
/// after <see cref="Dispose"/> the model steps of its jobs end in Error (a run in progress finishes first).</para>
/// <para><b>Texts</b>: the exception messages of this class, the step name of the good-part model (<see cref="GoodModelStepName"/>) and the value names its checks record
/// are Korean. Decide in code by exception type and by NG code (<see cref="VisionResult.Codes"/>), not by text.</para>
/// </remarks>
/// <example>
/// Load a recipe folder (or a .dvpkg package) and run it:
/// <code>
/// using var recipe = VisionRecipe.Load(@"C:\recipes\plate");
/// var job = recipe.CreateJob();
///
/// using var img = VisionImage.Load(path);
/// using var r = await job.RunAsync(img);
/// if (r.State == VisionState.Ng)
///     Console.WriteLine(string.Join(", ", r.Codes));   // for example "HOLE, LENGTH"
/// else if (r.State == VisionState.Error)
///     Console.WriteLine(r.Error);                      // could not inspect
/// </code>
/// </example>
public sealed partial class VisionRecipe : IDisposable
{
    /// <summary>The recipe file name in the recipe folder.</summary>
    public const string FileName = "studio.json";

    /// <summary>The newest file format version this library reads. Files are written as version 1 unless they use a good-part model or reason checks (version 2).</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>The name of the pose made by the alignment check (<c>r.Poses[VisionRecipe.PoseName]</c>). The ROIs of the checks after it follow this pose.</summary>
    public const string PoseName = "part";

    /// <summary>The step name of the good-part model in the job (<c>r.Steps[VisionRecipe.GoodModelStepName]</c>). A check cannot have this name.</summary>
    /// <remarks>The text is Korean and is part of the result format; the step records one value, the anomaly score. Use this constant instead of typing the text.</remarks>
    public const string GoodModelStepName = "양품 모델";

    /// <summary>The JSON options <c>studio.json</c> is written and read with (for example to serialize one <see cref="CheckDef"/> the same way).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 한글을 그대로
        AllowOutOfOrderMetadataProperties = true,                 // 손으로 고친 파일에서 Kind 가 맨 앞이 아니어도
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions BasisJson = new(JsonOptions) { WriteIndented = false };

    private readonly object _gate = new();
    private SharedModel<AnomalyModel>? _good;
    private readonly Dictionary<string, SharedModel<DefectModel>> _defects = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>The recipe name. It becomes the job name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The file format version (set when saving).</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>The base image path, relative to the recipe folder (images/base.png).</summary>
    public string? BaseImage { get; set; }

    /// <summary>The mm-per-pixel calibration, or <see langword="null"/> (lengths are then judged in px).</summary>
    public VisionCalibration? Calibration { get; set; }

    /// <summary>The good-part model, or <see langword="null"/> (the reference is the base image only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoodModel? GoodModel { get; set; }

    /// <summary>The checks, in running order. An alignment check, if any, is first.</summary>
    public List<CheckDef> Checks { get; set; } = [];

    /// <summary>The labelled test folders Studio runs. Not used when a job runs.</summary>
    public List<TestSet> TestSets { get; set; } = [];

    /// <summary>The recipe folder (not saved in the file).</summary>
    [JsonIgnore]
    public string Folder { get; private set; } = "";

    /// <summary>The full path of the base image file, or <see langword="null"/> when <see cref="BaseImage"/> is not set or the file does not exist.</summary>
    [JsonIgnore]
    public string? BaseImagePath
    {
        get
        {
            if (string.IsNullOrEmpty(BaseImage))
                return null;
            var path = Path.Combine(Folder, BaseImage);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Whether the good-part model was trained for the current alignment and region. <see langword="true"/> when there is no good-part model.</summary>
    [JsonIgnore]
    public bool GoodModelIsCurrent => GoodModel is not { } g || g.Basis == BasisOf(g.Roi);

    /// <summary>Makes a new, empty recipe (not saved yet).</summary>
    /// <param name="folder">The recipe folder.</param>
    /// <param name="name">The recipe name.</param>
    /// <returns>The new recipe.</returns>
    public static VisionRecipe Create(string folder, string name) => new() { Folder = Path.GetFullPath(folder), Name = name };

    /// <summary>Opens <c>studio.json</c> in a recipe folder, or a recipe package (.dvpkg, unpacked to the user's cache folder and checked against its manifest).</summary>
    /// <param name="folder">The recipe folder (a Studio project folder — the folder that holds studio.json) or a .dvpkg file.</param>
    /// <param name="loadModels">Read the model files now (default). <see langword="false"/> opens a recipe whose models are missing or not trained yet (Studio);
    /// <see cref="CreateJob"/> then reads them.</param>
    /// <returns>The recipe, with calibration, checks, the taught reference pose and the good-part model restored. Dispose it when its jobs are no longer used.</returns>
    /// <remarks>
    /// Call it once at startup or when the product changes, not per image. A package is unpacked once into the user's local application data folder
    /// (<c>Devinno.Vision\packages\&lt;hash&gt;</c>); <see cref="Folder"/> is then that folder and <see cref="PackagePath"/> the package file.
    /// </remarks>
    /// <example>
    /// <code>
    /// try
    /// {
    ///     using var recipe = VisionRecipe.Load(@"C:\deploy\plate.dvpkg");
    ///     var job = recipe.CreateJob();
    ///     // ... run the job for every image while the recipe is alive
    /// }
    /// catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
    /// {
    ///     Console.WriteLine(ex.Message);   // the message says what is wrong with the recipe
    /// }
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">There is no studio.json in the folder, the package file does not exist, or a model folder the recipe names is missing.</exception>
    /// <exception cref="InvalidDataException">The file content is wrong, was written by a newer format version, has a check kind this version does not know, a model file cannot be read,
    /// or a package does not match its manifest (it was changed after packing).</exception>
    public static VisionRecipe Load(string folder, bool loadModels = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string? package = null;
        if (folder.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(folder))
                throw new FileNotFoundException($"패키지가 없습니다: {Path.GetFullPath(folder)}", folder);
            package = Path.GetFullPath(folder);
            folder = Unpack(package);
        }
        var full = Path.GetFullPath(folder);
        var file = Path.Combine(full, FileName);
        if (!File.Exists(file))
            throw new FileNotFoundException($"{FileName} 이 없습니다: {full}", file);
        var text = File.ReadAllText(file);
        VisionRecipe? p;
        try
        {
            CheckKinds(text);
            p = JsonSerializer.Deserialize<VisionRecipe>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} 을 읽을 수 없습니다: {ex.Message}", ex);
        }
        if (p is null)
            throw new InvalidDataException($"{FileName} 이 비어 있습니다.");
        if (p.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"{FileName} 의 형식 버전({p.FormatVersion})이 이 라이브러리가 아는 버전({CurrentFormatVersion})보다 새것입니다. 라이브러리를 올리세요.");
        if (p.Checks is null || p.TestSets is null || p.TestSets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Folder)))
            throw new InvalidDataException($"{FileName} 의 Checks · TestSets 가 비었거나 잘못됐습니다.");
        if (p.Problem() is { } problem)
            throw new InvalidDataException($"{FileName}: {problem}");
        p.Folder = full;
        p.PackagePath = package;
        if (loadModels)
        {
            try
            {
                if (p.GoodModel is not null)
                    p.EnsureGoodModel();
                foreach (var o in p.Checks.OfType<OnnxCheck>().Where(o => !string.IsNullOrWhiteSpace(o.Model)))
                    p.DefectModelAt(o.Model);
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }
        return p;
    }

    // 검사 목록이 실행할 수 있는 모양인지 (파일에서 읽었든 코드로 만들었든 같은 규칙). 문제가 없으면 null.
    private string? Problem()
    {
        var names = new HashSet<string>();
        var ids = new HashSet<string>();
        foreach (var c in Checks)
        {
            if (c is null)
                return "빈 검사 항목이 있습니다.";
            if (c.Validate() is { } problem)
                return problem;
            if (!names.Add(c.Name))
                return $"같은 이름의 검사가 있습니다: {c.Name}";
            if (string.IsNullOrEmpty(c.Id) || !ids.Add(c.Id))
                return $"검사 id 가 비었거나 겹칩니다: {c.Name}";
        }
        if (Checks.Skip(1).Any(c => c.MakesPose))
            return "정렬 검사는 맨 앞에 하나만 둘 수 있습니다.";
        if (GoodModel is { } g)
        {
            if (string.IsNullOrWhiteSpace(g.Path))
                return "양품 모델의 Path 가 비었습니다.";
            if (g.Roi.IsEmpty)
                return "양품 모델의 영역(Roi)이 비었습니다.";
            if (!(g.Max >= 0) || double.IsInfinity(g.Max))
                return $"양품 모델의 문턱(Max)이 잘못됐습니다: {g.Max}";
            if (g.Code is null)
                return "양품 모델의 Code 가 없습니다.";
            if (names.Contains(GoodModelStepName))
                return $"검사 이름 '{GoodModelStepName}' 은 양품 모델 단계가 씁니다. 검사 이름을 바꾸세요.";
        }
        return null;
    }

    // 새 버전에서 만든 파일 — 판별자 예외 문구 대신 사람이 읽을 이유를 준다
    private static void CheckKinds(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject root || root["Checks"] is not JsonArray checks)
            return;
        foreach (var c in checks)
        {
            if (c is not JsonObject o || o["Kind"] is not JsonValue v || !v.TryGetValue<string>(out var kind))
                throw new InvalidDataException("검사 항목에 Kind 가 없습니다.");
            if (!CheckDef.Kinds.Contains(kind))
                throw new InvalidDataException($"알 수 없는 검사 종류: {kind} (이 버전이 아는 종류: {string.Join(", ", CheckDef.Kinds)})");
        }
    }

    /// <summary>Makes the job that runs this recipe: the alignment check, then the good-part model, then the other checks, with the calibration set.</summary>
    /// <returns>A new job. It holds copies of the current values, so editing the recipe afterwards does not change it — make a new job instead.</returns>
    /// <remarks>
    /// <para>The job is named after the recipe (<see cref="Name"/>; "studio" when the name is empty). Each check becomes one step named after the check,
    /// the good-part model becomes the step <see cref="GoodModelStepName"/>, and the alignment check makes the pose <see cref="PoseName"/> that the other ROIs follow.</para>
    /// <para>A job runs one image at a time (a second call while it runs returns Error "Busy"); for parallel runs call this method once per run. The jobs share the loaded models.</para>
    /// <para>A recipe with nothing to run would pass every part, so no job is made for it.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using var recipe = VisionRecipe.Load(folder);
    /// var job = recipe.CreateJob();
    /// job.Timeout = TimeSpan.FromSeconds(2);          // optional: time limit per step
    ///
    /// using var img = VisionImage.Load(path);
    /// using var r = await job.RunAsync(img);
    /// foreach (var v in r.Values)
    ///     Console.WriteLine($"{v.Step}: {v.Name} = {v.Value:0.##} {v.Unit} ({(v.Ok ? "OK" : v.Code)})");
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">The recipe has nothing to run, a check cannot run, or the good-part model must be trained again (the alignment or its region changed).</exception>
    /// <exception cref="FileNotFoundException">The good-part model folder is missing.</exception>
    /// <exception cref="InvalidDataException">The good-part model cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The recipe was disposed.</exception>
    public VisionJob CreateJob()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Checks.Count == 0 && GoodModel is null)
            throw new InvalidOperationException("레시피에 검사가 없습니다.");
        if (Problem() is { } problem)
            throw new InvalidOperationException(problem);
        var job = new VisionJob(string.IsNullOrWhiteSpace(Name) ? "studio" : Name) { Calibration = Calibration };
        var rest = Checks.AsEnumerable();
        if (Checks.Count > 0 && Checks[0].MakesPose)
        {
            Add(Checks[0]);
            rest = Checks.Skip(1);
        }
        if (GoodModel is { } g)
        {
            if (!GoodModelIsCurrent)
                throw new InvalidOperationException("양품 모델을 다시 학습하세요: 학습한 뒤에 정렬 검사 · 기준 위치 · 양품 모델 영역이 바뀌었습니다.");
            var model = EnsureGoodModel();
            var roi = g.Roi;
            job.Add(new GoodModelStep(model, g.Max, g.Code), roi: () => roi);
        }
        foreach (var check in rest)
            Add(check);
        return job;

        void Add(CheckDef check)
        {
            var roi = check.Roi;   // 지금 값 (뒤 편집과 무관하게)
            job.Add(check.CreateStep(this), roi: roi.IsEmpty ? null : () => roi, pose: check.MakesPose ? PoseName : null, role: check.Role);
        }
    }

    // 양품 모델을 읽어 둔다 (경로가 바뀌었으면 다시)
    private SharedModel<AnomalyModel> EnsureGoodModel()
    {
        var g = GoodModel ?? throw new InvalidOperationException("양품 모델이 없습니다.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_good is { } cur && cur.Path == g.Path)
                return cur;
            var dir = Path.GetFullPath(Path.Combine(Folder, g.Path));
            if (!Directory.Exists(dir))
                throw new FileNotFoundException($"양품 모델이 없습니다: {dir}", dir);
            var model = new AnomalyModel { Path = g.Path };
            try
            {
                model.Load(Folder);
            }
            catch (FileNotFoundException ex)
            {
                throw new FileNotFoundException($"양품 모델 파일이 없습니다: {ex.FileName ?? dir}", ex.FileName ?? dir, ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException($"양품 모델을 읽을 수 없습니다: {dir} ({ex.Message})", ex);
            }
            _good?.Dispose();
            _good = new SharedModel<AnomalyModel>(g.Path, model);
            return _good;
        }
    }

    // 불량 모델을 경로별로 읽어 둔다 (검사 속성을 고쳐 검사 객체가 바뀌어도 모델은 그대로)
    internal SharedModel<DefectModel> DefectModelAt(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_defects.TryGetValue(path, out var cur))
                return cur;
            var dir = Path.GetFullPath(Path.Combine(Folder, path));
            if (!Directory.Exists(dir))
                throw new FileNotFoundException($"불량 모델이 없습니다: {dir}", dir);
            DefectModel model;
            try
            {
                model = DefectModel.Load(dir);
            }
            catch (FileNotFoundException ex)
            {
                throw new FileNotFoundException($"불량 모델 파일이 없습니다: {ex.FileName ?? dir}", ex.FileName ?? dir, ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not InvalidDataException)
            {
                throw new InvalidDataException($"불량 모델을 읽을 수 없습니다: {dir} ({ex.Message})", ex);
            }
            return _defects[path] = new SharedModel<DefectModel>(path, model);
        }
    }

    /// <summary>
    /// Cuts a region out of an image the way a job does: the alignment check finds the part, then the region (base image coordinates) is cut where the part is.
    /// For preparing training images.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="roi">The region in base image coordinates.</param>
    /// <param name="ct">Cancels.</param>
    /// <returns>The cut image, owned by the caller (dispose it).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The part was not found (the message says why), or the region is empty.</exception>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public async Task<VisionImage> CropAsync(VisionImage image, VisionRoi roi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (roi.IsEmpty)
            throw new InvalidOperationException("자를 영역이 비었습니다.");
        VisionImage? captured = null;
        var cut = new VisionJob("crop") { Calibration = Calibration };
        if (Checks.Count > 0 && Checks[0] is BlobAlignCheck align)
            cut.Add(align.CreateStep(this), roi: align.Roi.IsEmpty ? null : () => align.Roi, pose: PoseName);
        cut.Add(new CaptureStep(img => captured = img), roi: () => roi);
        using var r = await cut.RunAsync(image, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (captured is not null && r.State == VisionState.Ok)
            return captured;
        captured?.Dispose();
        throw new InvalidOperationException(r.Error ?? r.State.ToString());
    }

    // 정렬 검사 · 기준 위치 · 영역 — 바뀌면 자른 이미지가 달라진다
    private string BasisOf(VisionRoi roi)
    {
        var align = Checks.Count > 0 && Checks[0] is BlobAlignCheck a ? a : null;
        return JsonSerializer.Serialize(new { Align = align is null ? null : new { align.Blob, align.Reference, align.Roi }, Roi = roi }, BasisJson);
    }

    /// <summary>
    /// Trains the good-part model from good images and sets it on the recipe (<see cref="GoodModel"/>, model files in a new models/good-… folder).
    /// Each image is cut out the way a job does (alignment, then the region), most images are learned, the rest set the limit.
    /// </summary>
    /// <param name="training">The images, feature model and settings.</param>
    /// <param name="progress">Reports each image.</param>
    /// <param name="ct">Cancels the training; the recipe is then unchanged.</param>
    /// <returns>What was learned, the limit and the time per image.</returns>
    /// <remarks>
    /// <para>Save the recipe to keep it. Until then the old model folder stays; <see cref="Save"/> removes good-model folders the recipe no longer uses.</para>
    /// <para>Jobs made before the training keep the old model, which is closed: make a new job with <see cref="CreateJob"/>.</para>
    /// <para>Learn the part, not the whole frame: set <see cref="GoodModelTraining.Roi"/> to the part. Use 100 or more good images when possible.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using var recipe = VisionRecipe.Load(folder, loadModels: false);
    /// var result = await recipe.TrainGoodModelAsync(new GoodModelTraining
    /// {
    ///     Images = Directory.GetFiles(@"C:\images\good", "*.png"),
    ///     Features = @"C:\models\features.onnx",
    ///     Roi = VisionRoi.Rect(100, 150, 470, 250),
    /// });
    /// recipe.Save();
    /// Console.WriteLine($"learned {result.Learned}, limit {result.Model.Max}, skipped {result.Skipped.Count}");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">Too few good images (3 or more are needed), or too few left after cutting.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GoodModelTraining.CalibrationShare"/> is not above 0 and at most 0.5.</exception>
    /// <exception cref="FileNotFoundException">The feature model does not exist.</exception>
    /// <exception cref="InvalidOperationException">The recipe has no folder.</exception>
    /// <exception cref="ObjectDisposedException">The recipe was disposed.</exception>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public async Task<GoodModelResult> TrainGoodModelAsync(GoodModelTraining training, IProgress<GoodModelProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(training);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(Folder))
            throw new InvalidOperationException("레시피 폴더가 없습니다.");
        if (training.Images is not { Count: >= 3 } images)
            throw new ArgumentException($"양품 이미지가 너무 적습니다 ({training.Images?.Count ?? 0}장, 최소 3장 — 100장 이상을 권합니다).", nameof(training));
        if (!File.Exists(training.Features))
            throw new FileNotFoundException($"특징 모델이 없습니다: {training.Features} (tools/export_features.py 로 만드세요)", training.Features);
        if (training.CalibrationShare is not (> 0 and <= 0.5))
            throw new ArgumentOutOfRangeException(nameof(training), "CalibrationShare 는 0 보다 크고 0.5 이하여야 합니다.");
        var total = Stopwatch.StartNew();

        var roi = training.Roi;
        if (roi.IsEmpty)
        {
            using var first = VisionImage.Load(images[0]);
            roi = VisionRoi.Rect(0, 0, first.Width, first.Height);
        }

        // 1. Job 과 같은 방식으로 자르기
        var crops = new List<(string File, VisionImage Image)>();
        var skipped = new List<(string File, string Reason)>();
        OnnxModel? features = null;
        AnomalyModel? model = null;
        try
        {
            for (var i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var img = VisionImage.Load(images[i]);
                    crops.Add((images[i], await CropAsync(img, roi, ct).ConfigureAwait(false)));
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or OpenCvSharp.OpenCVException
                                           || (ex is InvalidOperationException && ex is not ObjectDisposedException))
                {
                    skipped.Add((images[i], ex.Message));
                }
                progress?.Report(new GoodModelProgress("crop", i + 1, images.Count));
            }
            ct.ThrowIfCancellationRequested();
            if (crops.Count < 3)
                throw new ArgumentException($"쓸 수 있는 양품 이미지가 {crops.Count}장뿐입니다 (최소 3장). 뺀 이미지: {string.Join("; ", skipped.Take(3).Select(s => $"{Path.GetFileName(s.File)} {s.Reason}"))}", nameof(training));

            // 2. 기억용 · 보정용 나누기 (고르게 띄워서)
            var calCount = Math.Max(1, (int)Math.Round(crops.Count * training.CalibrationShare));
            var memory = new List<VisionImage>();
            var calibration = new List<VisionImage>();
            for (var i = 0; i < crops.Count; i++)
                ((long)i * calCount / crops.Count != (long)(i + 1) * calCount / crops.Count ? calibration : memory).Add(crops[i].Image);

            // 3. 학습
            features = OnnxModel.Load(training.Features, training.InputSize, training.InputSize, VisionTensorFormat.ImageNet);
            var learn = new Relay(n => progress?.Report(new GoodModelProgress("learn", n, memory.Count)));
            model = AnomalyModel.Train(features, memory, training.Representatives, training.Neighborhood, learn, ct);

            // 4. 문턱 — 학습에 안 쓴 양품의 최고 점수 × 여유
            var scores = new List<double>();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < calibration.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                scores.Add(model.Score(calibration[i]).Score);
                progress?.Report(new GoodModelProgress("limit", i + 1, calibration.Count));
            }
            var ms = watch.Elapsed.TotalMilliseconds / calibration.Count;
            var max = Math.Round(scores.Max() * training.Margin, 3);

            // 5. 새 폴더에 저장하고 레시피에 건다 (옛 폴더는 Save 가 정리)
            var rel = $"models/good-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (var n = 2; Directory.Exists(Path.Combine(Folder, rel)); n++)
                rel = $"models/good-{DateTime.Now:yyyyMMdd-HHmmss}-{n}";
            model.Save(Folder, rel);
            var g = new GoodModel { Path = rel, Roi = roi, Max = max, Code = training.Code, Basis = BasisOf(roi) };
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _good?.Dispose();
                _good = new SharedModel<AnomalyModel>(rel, model);
                GoodModel = g;
            }
            model = null;      // 레시피가 가졌다
            features = null;   // 모델이 가졌다
            return new GoodModelResult(g, memory.Count, scores, skipped, ms, total.Elapsed);
        }
        finally
        {
            foreach (var c in crops)
                c.Image.Dispose();
            if (model is not null)
                model.Dispose();      // 특징 모델도 같이 닫힌다
            else
                features?.Dispose();
        }
    }

    // 진행률을 바로 넘긴다 (Progress<T> 는 다른 스레드로 넘겨 순서가 섞일 수 있다)
    private sealed class Relay(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // 자른 영역을 들고 나온다 (실행이 끝나도 남게 복사)
    private sealed class CaptureStep(Action<VisionImage> take) : IVisionStep
    {
        public string Name => "crop";

        public Task RunAsync(VisionContext ctx)
        {
            take(ctx.Image.Clone());
            return Task.CompletedTask;
        }
    }

    /// <summary>Reads the base image the recipe was taught on (for display or teaching again; running a job does not need it).</summary>
    /// <returns>The image. The caller disposes it.</returns>
    /// <exception cref="InvalidOperationException"><see cref="BaseImage"/> is not set.</exception>
    /// <exception cref="FileNotFoundException">The base image file does not exist.</exception>
    public VisionImage LoadBaseImage()
    {
        if (string.IsNullOrEmpty(BaseImage))
            throw new InvalidOperationException("레시피에 기준 이미지가 지정되지 않았습니다.");
        var path = Path.GetFullPath(Path.Combine(Folder, BaseImage));
        if (!File.Exists(path))
            throw new FileNotFoundException($"기준 이미지가 없습니다: {path}", path);
        return VisionImage.Load(path);
    }

    /// <summary>
    /// Saves to studio.json in <see cref="Folder"/> (indented). The folder is created if missing. The format version is 2 when the recipe uses a good-part model,
    /// reason checks or ONNX checks, otherwise 1. Model folders made by training (models/good-…, models/defect-…) the recipe no longer uses are removed.
    /// </summary>
    /// <remarks>
    /// The text is written to a temporary file first and then moved over studio.json, so an interrupted save leaves the old file.
    /// A model folder another program holds open is left and removed at a later save.
    /// </remarks>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        FormatVersion = Version();
        var file = Path.Combine(Folder, FileName);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, file, overwrite: true);   // 쓰다 끊겨도 원래 파일은 남게
        RemoveUnusedModels();
    }

    // 학습 · 가져오기가 만든 폴더(models/good-… · models/defect-…) 중 레시피가 안 쓰는 것
    private void RemoveUnusedModels()
    {
        var models = Path.Combine(Folder, "models");
        if (!Directory.Exists(models))
            return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (GoodModel is { } g)
            used.Add(Path.GetFullPath(Path.Combine(Folder, g.Path)));
        foreach (var o in Checks.OfType<OnnxCheck>().Where(o => !string.IsNullOrWhiteSpace(o.Model)))
            used.Add(Path.GetFullPath(Path.Combine(Folder, o.Model)));
        foreach (var dir in Directory.EnumerateDirectories(models, "good-*").Concat(Directory.EnumerateDirectories(models, "defect-*")))
        {
            if (used.Contains(Path.GetFullPath(dir)))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다른 프로그램이 쥐고 있으면 다음 저장 때 다시 지운다
            }
        }
    }

    /// <summary>Releases the loaded models. Jobs made from this recipe then fail at the model step (Error); a run in progress finishes first.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _good?.Dispose();
            _good = null;
            foreach (var d in _defects.Values)
                d.Dispose();
            _defects.Clear();
        }
    }
}
