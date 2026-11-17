using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Devinno.Vision;

// AI 문서 묶음(llms.txt · llm-rules.md · vision-*.md · 패키지별 llms.txt)을 지키는 테스트 —
// 링크 · 앵커가 맞는지, 본문이 영어인지(한글은 코드 표기 안에서만), 문서에 나온 API 이름이 실제 공개 멤버인지.
// 예제 코드가 컴파일되고 도는지는 DocExamplesTests 가 본다.
public class AiDocsTests
{
    private static string Root([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "llm-rules.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (llm-rules.md)");
    }

    // 패키지 docs/ 에 같이 들어가는 문서 (저장소 루트)
    private static string[] CoreDocs()
        => ["llms.txt", "llm-rules.md", .. Directory.EnumerateFiles(Root(), "vision-*.md").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    private static string[] AllDocs()
        => [.. CoreDocs(), .. Directory.EnumerateFiles(Path.Combine(Root(), "src"), "llms.txt", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(f, @"[\\/](obj|bin|\.devinno)[\\/]"))   // .devinno = 참조한 Devinno 패키지의 문서 사본
            .Select(f => Path.GetRelativePath(Root(), f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    private static string Text(string doc) => File.ReadAllText(Path.Combine(Root(), doc)).Replace("\r\n", "\n");

    private static readonly Regex AnchorRx = new("<a id=\"([^\"]+)\"></a>");
    private static readonly Regex LinkRx = new(@"\[[^\]]*\]\(([^)\s]+)\)");

    [Fact]
    public void The_doc_set_is_complete()
    {
        var docs = AllDocs();
        Assert.Contains("vision-recipe.md", docs);
        Assert.Contains("vision-jobs.md", docs);
        Assert.Contains("vision-toolkit.md", docs);
        Assert.Contains("src/Devinno.Vision.UI/llms.txt", docs);
        Assert.Contains("src/Devinno.Vision.FlashCap/llms.txt", docs);
        // llms.txt 가 모든 페이지를 가리킨다
        var map = Text("llms.txt");
        foreach (var page in CoreDocs().Where(d => d != "llms.txt"))
            Assert.Contains($"({page}", map);
        // 첫 줄의 버전 자리 (pack 때 채워진다)
        foreach (var llms in docs.Where(d => d.EndsWith("llms.txt")))
            Assert.Contains("{{VERSION}}", Text(llms).Split('\n')[0]);
    }

    [Fact]
    public void Links_and_anchors_resolve()
    {
        var anchors = CoreDocs().ToDictionary(d => d, d => AnchorRx.Matches(Text(d)).Select(m => m.Groups[1].Value).ToHashSet());
        var problems = new List<string>();
        foreach (var doc in CoreDocs())
        {
            foreach (Match m in LinkRx.Matches(Text(doc)))
            {
                var target = m.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.Ordinal))
                    continue;
                var parts = target.Split('#');
                var file = parts[0].Length == 0 ? doc : parts[0];
                if (!anchors.TryGetValue(file, out var ids))
                    problems.Add($"{doc}: link to a file outside the doc set: {target}");
                else if (parts.Length > 1 && !ids.Contains(parts[1]))
                    problems.Add($"{doc}: no anchor '{parts[1]}' in {file}");
            }
            var dup = AnchorRx.Matches(Text(doc)).Select(m => m.Groups[1].Value).GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0)
                problems.Add($"{doc}: duplicate anchors: {string.Join(", ", dup)}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // 문서는 영어가 기준이다. 한글은 결과 형식의 문자열(값 이름 · 오류 문구)을 코드 표기로 보일 때만
    [Fact]
    public void Prose_is_English()
    {
        var problems = new List<string>();
        foreach (var doc in AllDocs())
        {
            var inBlock = false;
            var lines = Text(doc).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inBlock = !inBlock;
                    continue;
                }
                if (inBlock)
                    continue;
                var prose = Regex.Replace(lines[i], "`[^`]*`", "");
                if (Regex.IsMatch(prose, "[가-힣]"))
                    problems.Add($"{doc}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(problems.Count == 0, "Korean outside code spans:\n" + string.Join("\n", problems));
    }

    // 문서에 Type.Member 로 나온 이름은 실제 공개 멤버여야 한다 ("문서에 없는 것은 존재하지 않는다"의 반대 방향)
    [Fact]
    public void Api_names_in_the_docs_exist()
    {
        var types = typeof(VisionImage).Assembly.GetExportedTypes().Where(t => !t.IsGenericType && !t.IsNested).ToDictionary(t => t.Name);
        var aliases = new Dictionary<string, Type[]>
        {
            ["ctx"] = [typeof(VisionContext)],
            ["recipe"] = [typeof(VisionRecipe)],
            ["r"] = [typeof(VisionResult)],
            ["job"] = [typeof(VisionJob), typeof(VisionJob<>), typeof(VisionJob<,>)],
        };
        var rx = new Regex(@"(?<![\w.])([A-Za-z]\w*)\.([A-Z]\w*)");
        var problems = new SortedSet<string>();
        var seen = 0;
        foreach (var doc in CoreDocs())
        {
            foreach (Match m in rx.Matches(Text(doc)))
            {
                var (owner, member) = (m.Groups[1].Value, m.Groups[2].Value);
                Type[] candidates = aliases.TryGetValue(owner, out var a) ? a : types.TryGetValue(owner, out var t) ? [t] : [];
                if (candidates.Length == 0)
                    continue;
                seen++;
                if (!candidates.Any(c => Has(c, member)))
                    problems.Add($"{doc}: {owner}.{member}");
            }
        }
        Assert.True(seen > 100, $"only {seen} API references found — the scan is broken");
        Assert.True(problems.Count == 0, "Not public members:\n" + string.Join("\n", problems));

        static bool Has(Type type, string name)
            => type.GetMember(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy).Length > 0
               || type.GetInterfaces().Any(i => i.GetMember(name).Length > 0);
    }

    // 문서가 그대로 적은 문자열 · 기본값이 코드와 같은지
    [Fact]
    public void Literals_in_the_docs_match_the_code()
    {
        var recipe = Text("vision-recipe.md");
        Assert.Contains($"`\"{VisionRecipe.GoodModelStepName}\"`", recipe);
        Assert.Contains($"`\"{VisionRecipe.PoseName}\"`", recipe);
        Assert.Contains($"`\"{VisionRecipe.PackageExtension}\"`", recipe);
        Assert.Contains($"`\"{VisionRecipe.ManifestFile}\"`", recipe);
        Assert.Contains($"`\"{VisionRecipe.FileName}\"`", recipe);
        Assert.Contains($"`VisionRecipe.CurrentFormatVersion` ({VisionRecipe.CurrentFormatVersion})", recipe);
        foreach (var kind in Devinno.Vision.Recipes.CheckDef.Kinds)
        {
            Assert.Contains($"| `{kind}` | `{kind}Check` |", recipe);
            Assert.Contains($"`{kind}`", Text("llm-rules.md"));
        }
    }
}
