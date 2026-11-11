using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Devinno.Vision.Recipes;

namespace Devinno.Vision;

/// <summary>
/// An inspection made in Studio: a folder with <c>studio.json</c> (checks, calibration, taught reference pose), the base image and model files.
/// An app loads the folder and runs the same inspection without Studio.
/// </summary>
/// <example>
/// <code>
/// var recipe = VisionRecipe.Load(@"C:\recipes\bracket");
/// var job = recipe.CreateJob();
/// using var img = VisionImage.Load(path);
/// using var r = await job.RunAsync(img);
/// </code>
/// </example>
public sealed class VisionRecipe
{
    /// <summary>The recipe file name in the recipe folder.</summary>
    public const string FileName = "studio.json";

    /// <summary>The file format version this library reads and writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The name of the pose made by the alignment check (<c>r.Poses[VisionRecipe.PoseName]</c>). The ROIs of the checks after it follow this pose.</summary>
    public const string PoseName = "part";

    /// <summary>The JSON options <c>studio.json</c> is written and read with (for example to serialize one <see cref="CheckDef"/> the same way).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 한글을 그대로
        AllowOutOfOrderMetadataProperties = true,                 // 손으로 고친 파일에서 Kind 가 맨 앞이 아니어도
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The recipe name. It becomes the job name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The file format version.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The base image path, relative to the recipe folder (images/base.png).</summary>
    public string? BaseImage { get; set; }

    /// <summary>The mm-per-pixel calibration, or <see langword="null"/> (lengths are then judged in px).</summary>
    public VisionCalibration? Calibration { get; set; }

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

    /// <summary>Makes a new, empty recipe (not saved yet).</summary>
    /// <param name="folder">The recipe folder.</param>
    /// <param name="name">The recipe name.</param>
    /// <returns>The new recipe.</returns>
    public static VisionRecipe Create(string folder, string name) => new() { Folder = Path.GetFullPath(folder), Name = name };

    /// <summary>Opens <c>studio.json</c> in a recipe folder.</summary>
    /// <param name="folder">The recipe folder (a Studio project folder).</param>
    /// <returns>The recipe, with calibration, checks and the taught reference pose restored.</returns>
    /// <exception cref="FileNotFoundException">There is no studio.json in the folder.</exception>
    /// <exception cref="InvalidDataException">The file content is wrong, was written by a newer format version, or has a check kind this version does not know.</exception>
    public static VisionRecipe Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
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

    /// <summary>Makes the job that runs this recipe: one step per check, in order, with the calibration set.</summary>
    /// <returns>A new job. It holds copies of the current values, so editing the recipe afterwards does not change it — make a new job instead.</returns>
    /// <exception cref="InvalidOperationException">The recipe has no checks, or a check cannot run (for example an empty name or two checks with one name).</exception>
    public VisionJob CreateJob()
    {
        if (Checks.Count == 0)
            throw new InvalidOperationException("레시피에 검사가 없습니다.");
        if (Problem() is { } problem)
            throw new InvalidOperationException(problem);
        var job = new VisionJob(string.IsNullOrWhiteSpace(Name) ? "studio" : Name) { Calibration = Calibration };
        foreach (var check in Checks)
        {
            var roi = check.Roi;   // 지금 값 (뒤 편집과 무관하게)
            job.Add(check.CreateStep(this), roi: roi.IsEmpty ? null : () => roi, pose: check.MakesPose ? PoseName : null);
        }
        return job;
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

    /// <summary>Saves to studio.json in <see cref="Folder"/> (indented). The folder is created if missing.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, FileName);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, file, overwrite: true);   // 쓰다 끊겨도 원래 파일은 남게
    }
}
