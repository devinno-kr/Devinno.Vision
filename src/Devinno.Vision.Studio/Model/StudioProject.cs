using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Devinno.Vision.Studio.Model;

/// <summary>Studio 프로젝트 — 폴더 하나에 studio.json 으로 저장된다.</summary>
public sealed class StudioProject
{
    public const string FileName = "studio.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 한글을 그대로
        AllowOutOfOrderMetadataProperties = true,                 // 손으로 고친 파일에서 Kind 가 맨 앞이 아니어도
        Converters = { new JsonStringEnumConverter() },
    };

    public string Name { get; set; } = "";

    public int FormatVersion { get; set; } = 1;

    /// <summary>프로젝트 폴더 기준 상대 경로 (images/base.png).</summary>
    public string? BaseImage { get; set; }

    public VisionCalibration? Calibration { get; set; }

    public List<CheckDef> Checks { get; set; } = [];

    public List<TestSet> TestSets { get; set; } = [];

    /// <summary>프로젝트 폴더 (저장하지 않음).</summary>
    [JsonIgnore]
    public string Folder { get; internal set; } = "";

    /// <summary>새 프로젝트 (아직 저장 안 함).</summary>
    public static StudioProject Create(string folder, string name) => new() { Folder = Path.GetFullPath(folder), Name = name };

    /// <summary>folder/studio.json 을 연다.</summary>
    /// <exception cref="FileNotFoundException">studio.json 이 없다.</exception>
    /// <exception cref="InvalidDataException">파일 내용이 잘못됐거나, 이 버전이 모르는 검사 종류가 있다.</exception>
    public static StudioProject Load(string folder)
    {
        var full = Path.GetFullPath(folder);
        var file = Path.Combine(full, FileName);
        if (!File.Exists(file))
            throw new FileNotFoundException($"{FileName} 이 없습니다: {full}", file);
        var text = File.ReadAllText(file);
        StudioProject? p;
        try
        {
            CheckKinds(text);
            p = JsonSerializer.Deserialize<StudioProject>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} 을 읽을 수 없습니다: {ex.Message}", ex);
        }
        if (p is null)
            throw new InvalidDataException($"{FileName} 이 비어 있습니다.");
        p.Folder = full;
        return p;
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

    /// <summary>folder/studio.json 에 저장한다 (들여쓰기). 폴더가 없으면 만든다.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, FileName);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, overwrite: true);   // 쓰다 끊겨도 원래 파일은 남게
    }
}
