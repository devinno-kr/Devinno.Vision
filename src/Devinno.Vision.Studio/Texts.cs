using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devinno.UI.Localization;
using Devinno.Vision.Recipes;

namespace Devinno.Vision.Studio;

/// <summary>
/// 화면 · 명령 결과 문구 (한국어 기본, 영어 선택). 문자열 표는 실행 폴더의 Strings/*.json — 파일 이름의 마지막 점 뒤(확장자 앞)가 언어다.
/// 공통 키(언어 이름 · 검사 종류 이름 · 앱 틀)는 Strings/&lt;언어&gt;.json, 소스 파일 하나에서만 쓰는 키는 Strings/&lt;이름&gt;.&lt;언어&gt;.json
/// (예: EditPage.ko.json). 같은 쌍(ko · en)의 키와 자리 표시({0}, {1} …)는 같아야 한다 (LanguageTests 가 확인).
/// 마크업은 <c>@키</c> · <c>{L10n.키}</c> 로, 코드(화면의 상태 글 · 명령 결과 · 대화상자)는 <see cref="T(string)"/> 로 읽는다.
/// 명령 층의 결과 문구는 MCP 응답으로도 나가므로, 언어를 바꾸면 MCP 응답도 그 언어가 된다.
/// </summary>
/// <remarks>
/// 번역하지 않는 것: 레시피가 만드는 값 이름 · 오류 문구("왼쪽 구멍 개수", "부품 없음")와 라이브러리의 예외 문구 — 결과 형식이라 한국어 그대로다.
/// </remarks>
public static class Texts
{
    /// <summary>고를 수 있는 언어 (표시 순서).</summary>
    public static readonly string[] Languages = ["ko", "en"];

    /// <summary>처음 실행할 때의 언어.</summary>
    public const string DefaultLanguage = "ko";

    private static string? s_settingsFile;

    /// <summary>문자열 표 — <c>app.Strings</c> 에 건다. 언어를 바꾸면 화면이 다시 그려진다.</summary>
    public static DvStrings Strings { get; } = Load();

    /// <summary>지금 언어 ("ko" · "en").</summary>
    public static string Language => Strings.Language;

    /// <summary>지금 언어의 문구.</summary>
    public static string T(string key) => Strings.Get(key);

    /// <summary>지금 언어의 문구에 값을 채운다 (<c>{0}</c>, <c>{1:0.0}</c> …). 숫자 표기는 문자열 보간과 같이 현재 문화권을 따른다.</summary>
    public static string T(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Strings.Get(key), args);

    /// <summary>검사 종류의 화면 이름 ("블롭 개수" · "Blob count"). 라이브러리의 <see cref="CheckDef.KindLabel"/> 은 한국어뿐이라 Studio 가 따로 가진다.</summary>
    public static string KindLabel(CheckDef check) => T("kind." + check.Kind);

    /// <summary>언어를 바꾼다. 설정 파일을 쓰고 있으면(<see cref="UseSettings"/>) 저장해서 다음 실행에도 유지한다.</summary>
    /// <returns>바꿨으면 true. 모르는 언어면 false (그대로 둔다).</returns>
    public static bool SetLanguage(string language)
    {
        if (!Languages.Contains(language))
            return false;
        Strings.Language = language;   // 화면이 붙어 있으면 다시 그려진다
        if (s_settingsFile is { } file)
            SaveLanguage(file, language);
        return true;
    }

    /// <summary>설정 파일을 정하고, 저장된 언어가 있으면 그 언어로 바꾼다. 프로그램 시작 때 한 번 (테스트는 부르지 않는다 — 사용자 설정을 건드리지 않게).</summary>
    public static void UseSettings(string file)
    {
        s_settingsFile = file;
        if (ReadLanguage(file) is { } saved && Languages.Contains(saved))
            Strings.Language = saved;
    }

    /// <summary>이 PC 사용자의 Studio 설정 파일 (%LOCALAPPDATA%\Devinno.Vision.Studio\settings.json).</summary>
    public static string DefaultSettingsFile
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Devinno.Vision.Studio", "settings.json");

    /// <summary>설정 파일에 저장된 언어. 파일이 없거나 읽을 수 없으면 null.</summary>
    public static string? ReadLanguage(string file)
    {
        try
        {
            return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o ? (string?)o["Language"] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;   // 깨진 설정 파일 때문에 프로그램이 안 켜지면 안 된다
        }
    }

    /// <summary>설정 파일에 언어를 쓴다 (다른 항목은 그대로 둔다). 쓸 수 없으면 조용히 넘어간다 — 언어는 이번 실행 동안은 바뀐 채다.</summary>
    public static void SaveLanguage(string file, string language)
    {
        try
        {
            JsonObject o;
            try
            {
                o = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject cur ? cur : [];
            }
            catch (JsonException)
            {
                o = [];
            }
            o["Language"] = language;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>문자열 표 폴더.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Strings");

    private static DvStrings Load()
    {
        var strings = new DvStrings { Language = DefaultLanguage, FallbackLanguage = DefaultLanguage };
        // 모든 Strings/*.json 을 읽는다 — LoadJson 은 항목을 더하므로 언어마다 여러 번 불러도 된다
        foreach (var file in Directory.GetFiles(Folder, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var lang = name[(name.LastIndexOf('.') + 1)..];   // "EditPage.en" → en, "en" → en
            strings.LoadJson(lang, File.ReadAllText(file));
        }
        return strings;
    }
}
