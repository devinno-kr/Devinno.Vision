using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.Studio.Ui;
using Xunit;

namespace Devinno.Vision.Studio.Tests;

// 화면 문구 표 (Strings/*.json) — 한국어 · 영어 쌍이 맞는지, 소스가 쓰는 키가 다 있는지, 소스에 한글 문구가 남지 않았는지.
// 전역 언어를 바꾸지 않는 검사만 여기 둔다 (다른 테스트는 한국어 문구를 본다).
public class LanguageTableTests
{
    private static string StudioDir([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "src", "Devinno.Vision.Studio"));

    private static Dictionary<string, string> Table(string lang)
    {
        var all = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(Path.Combine(StudioDir(), "Strings"), $"*{lang}.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name[(name.LastIndexOf('.') + 1)..] != lang)
                continue;
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!)
                Assert.True(all.TryAdd(k, v), $"key '{k}' is in two tables ({Path.GetFileName(file)})");
        }
        return all;
    }

    private static List<string> Placeholders(string text)
        => [.. Regex.Matches(text.Replace("{{", "").Replace("}}", ""), @"\{(\d+)(:[^}]*)?\}").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    private static IEnumerable<string> Sources(string pattern)
        => Directory.EnumerateFiles(StudioDir(), pattern, SearchOption.AllDirectories).Where(f => !Regex.IsMatch(f, @"[\\/](obj|bin)[\\/]"));

    [Fact]
    public void Korean_and_English_tables_have_the_same_keys_and_placeholders()
    {
        var (ko, en) = (Table("ko"), Table("en"));
        Assert.True(ko.Count > 200, $"only {ko.Count} keys");
        Assert.Equal(ko.Keys.Order(StringComparer.Ordinal), en.Keys.Order(StringComparer.Ordinal));
        var problems = ko.Keys.Where(k => !Placeholders(ko[k]).SequenceEqual(Placeholders(en[k]))).Select(k => $"{k}: ko [{ko[k]}] en [{en[k]}]").ToList();
        Assert.True(problems.Count == 0, "placeholders differ:\n" + string.Join("\n", problems));
        var empty = en.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList();
        Assert.True(empty.Count == 0, "empty English text: " + string.Join(", ", empty));
        var korean = en.Where(p => p.Key != "lang.ko" && Regex.IsMatch(p.Value, "[가-힣]")).Select(p => p.Key).ToList();
        Assert.True(korean.Count == 0, "Korean in the English table: " + string.Join(", ", korean));
    }

    [Fact]
    public void Every_key_the_source_uses_is_in_the_tables()
    {
        var ko = Table("ko");
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Sources("*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bT\(\s*""([A-Za-z0-9_.]+)""\s*[,)]"))
                used.Add(m.Groups[1].Value);
        foreach (var file in Sources("*.dvml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"=""@([A-Za-z0-9_.]+)"""))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\{L10n\.([A-Za-z0-9_.]+)\}"))
                used.Add(m.Groups[1].Value);
        }
        Assert.True(used.Count > 150, $"only {used.Count} keys found in the source — the scan is broken");
        var missing = used.Where(k => !ko.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "keys used but not in Strings/*.ko.json:\n" + string.Join("\n", missing));
    }

    // 화면 문구가 소스에 한글로 남으면 영어 화면에 한글이 섞인다. 주석은 한글 그대로다.
    [Fact]
    public void No_Korean_text_is_left_in_the_source()
    {
        var problems = new List<string>();
        foreach (var file in Sources("*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = Regex.Replace(lines[i], @"//.*$", "");
                foreach (Match m in Regex.Matches(code, "\"(?:[^\"\\\\]|\\\\.)*\""))
                    if (Regex.IsMatch(m.Value, "[가-힣]"))
                        problems.Add($"{Path.GetFileName(file)}:{i + 1}: {m.Value}");
            }
        }
        foreach (var file in Sources("*.dvml"))
        {
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], "[가-힣]"))
                    problems.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(problems.Count == 0, "Korean text in the source (move it to Strings/*.json):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Language_setting_is_saved_and_read_back()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "sub", "settings.json");
        Assert.Null(Texts.ReadLanguage(file));
        Texts.SaveLanguage(file, "en");
        Assert.Equal("en", Texts.ReadLanguage(file));
        File.WriteAllText(file, """{ "Other": 1, "Language": "ko" }""");
        Texts.SaveLanguage(file, "en");
        Assert.Contains("\"Other\": 1", File.ReadAllText(file));   // 다른 항목은 그대로
        Assert.Equal("en", Texts.ReadLanguage(file));
        File.WriteAllText(file, "not json");
        Assert.Null(Texts.ReadLanguage(file));                      // 깨진 파일 → 기본 언어
        Texts.SaveLanguage(file, "ko");
        Assert.Equal("ko", Texts.ReadLanguage(file));
    }
}

// 전역 언어를 바꾸는 테스트 — 다른 테스트(한국어 문구를 본다)와 같이 돌지 않게 병렬 실행을 끈 묶음에 둔다.
[CollectionDefinition("Language", DisableParallelization = true)]
public class LanguageCollection;

[Collection("Language")]
public class LanguageSwitchTests
{
    [Fact]
    public void Switching_changes_screen_and_command_texts_and_back()
    {
        Assert.Equal("ko", Texts.Language);
        try
        {
            using var dir = new TempDir();
            var session = new StudioSession();
            var app = StudioApp.Create(session, new NullDialogs());
            var top = (TopBar)app.Top!;
            var status = (StatusBar)app.Bottom!;
            Assert.Equal("준비됨", status.Notice);
            Assert.StartsWith("로직이 없습니다", top.FolderText);
            Assert.Equal("프로젝트를 먼저 만들거나 여세요", session.SaveProject().Message);

            top.Language = "en";
            Assert.Equal("en", Texts.Language);
            Assert.Equal("Ready", status.Notice);
            Assert.StartsWith("No project", top.FolderText);
            var refused = session.SaveProject();
            Assert.False(refused.Ok);
            Assert.DoesNotMatch("[가-힣]", refused.Message);
            Assert.True(session.NewProject(dir.Sub("p"), "bracket").Ok);
            var added = session.AddCheck("BlobCount", "holes");
            Assert.True(added.Ok);
            Assert.Contains("Blob count", added.Message);
            Assert.DoesNotMatch("[가-힣]", added.Message);
            Assert.Equal("Blob count", Texts.KindLabel(session.Checks[0]));
            var png = Png.Render(app.Surface, 1280, 800);          // 영어 화면이 그려진다 (없는 키가 있으면 [key] 로 보인다)
            Assert.True(png.Length > 1000);

            Assert.False(Texts.SetLanguage("fr"));                  // 모르는 언어는 그대로
            Assert.Equal("en", Texts.Language);

            // 검사가 든 프로젝트로 두 화면을 두 언어로 그린다. DV_STUDIO_SHOTS 폴더를 주면 PNG 를 남긴다 (눈으로 확인용)
            var bracket = new StudioSession();
            BracketStudioTests.BuildBracket(bracket, dir.Sub("bracket"));
            var app2 = StudioApp.Create(bracket, new NullDialogs());
            ((CheckListBar)app2.Left!).SelectedId = bracket.Checks[1].Id;
            var shots = Environment.GetEnvironmentVariable("DV_STUDIO_SHOTS");
            foreach (var lang in new[] { "en", "ko" })
            {
                Texts.SetLanguage(lang);
                foreach (var page in app2.Pages)
                {
                    app2.Show(page);
                    app2.Surface.Layout(1280, 800);
                    var shot = Png.Render(app2.Surface, 1280, 800);
                    Assert.True(shot.Length > 1000);
                    if (!string.IsNullOrEmpty(shots))
                        File.WriteAllBytes(Path.Combine(shots, $"{lang}-{page.GetType().Name}.png"), shot);
                }
            }
            Texts.SetLanguage("en");
        }
        finally
        {
            Texts.SetLanguage("ko");
        }
        Assert.Equal("ko", Texts.Language);
    }
}
