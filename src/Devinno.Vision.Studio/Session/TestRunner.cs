using Devinno.Vision.Studio.Model;

namespace Devinno.Vision.Studio.Session;

/// <summary>폴더 테스트 보고서.</summary>
/// <param name="Ok">테스트를 돌렸는가 (정답률과 무관).</param>
/// <param name="Message">요약 또는 못 돌린 이유.</param>
/// <param name="Labels">라벨(종류)별 표.</param>
/// <param name="Items">이미지별 결과 (파일 이름 순).</param>
/// <param name="Correct">맞힌 장 수.</param>
/// <param name="Total">돌린 장 수.</param>
public sealed record TestReport(bool Ok, string Message, IReadOnlyList<LabelRow> Labels, IReadOnlyList<TestItem> Items, int Correct, int Total);

/// <summary>종류별 표 한 줄.</summary>
public sealed record LabelRow(string Label, bool ExpectOk, int Count, int Correct);

/// <summary>이미지 한 장의 테스트 결과. 정답 = State 가 Error 가 아니고 (기대 OK) == (State == "Ok").</summary>
public sealed record TestItem(string File, string Label, bool ExpectOk, string State, IReadOnlyList<string> Codes, string? Error, bool Correct);

/// <summary>테스트 폴더의 이미지를 모두 돌려 보고서를 만든다.</summary>
internal static class TestRunner
{
    public static readonly string[] ImageExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];

    public static TestReport Fail(string message) => new(false, message, [], [], 0, 0);

    /// <summary>폴더에서 (파일, 라벨) 목록을 읽는다. 이미지 확장자가 아닌 파일은 건너뛴다.</summary>
    public static List<(string File, string Label)> Collect(TestSet set)
    {
        var list = new List<(string, string)>();
        if (set.Rule == LabelRule.Folder)
        {
            foreach (var sub in Directory.EnumerateDirectories(set.Folder).Order(StringComparer.Ordinal))
                foreach (var f in Images(sub))
                    list.Add((f, Path.GetFileName(sub)));
        }
        else
        {
            foreach (var f in Images(set.Folder))
                list.Add((f, LabelFromName(f)));
        }
        return list;
    }

    // 02_missinghole.bmp → missinghole (첫 '_' 뒤부터 다음 '_' 또는 확장자 앞까지). '_' 가 없으면 파일 이름 전체
    internal static string LabelFromName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var i = name.IndexOf('_');
        if (i < 0)
            return name;
        var rest = name[(i + 1)..];
        var j = rest.IndexOf('_');
        return j < 0 ? rest : rest[..j];
    }

    private static IEnumerable<string> Images(string folder) => Directory.EnumerateFiles(folder)
        .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .Order(StringComparer.Ordinal);

    /// <summary>한 장씩 돌린다. 한 장의 실패(깨진 이미지 등)는 그 항목만 Error 로 남기고 계속한다.</summary>
    public static async Task<TestReport> RunAsync(VisionJob job, TestSet set, IProgress<int>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(set.Folder))
            return Fail($"테스트 폴더가 없습니다: {set.Folder}");
        List<(string File, string Label)> files;
        try
        {
            files = Collect(set);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"테스트 폴더를 읽을 수 없습니다: {ex.Message}");
        }
        if (files.Count == 0)
            return Fail("이미지 없음");

        var items = new List<TestItem>(files.Count);
        foreach (var (file, label) in files)
        {
            ct.ThrowIfCancellationRequested();
            var expectOk = string.Equals(label, set.GoodLabel, StringComparison.OrdinalIgnoreCase);
            items.Add(await RunOne(job, file, label, expectOk, ct).ConfigureAwait(false));
            progress?.Report(items.Count);
        }

        var labels = items.GroupBy(i => i.Label, StringComparer.Ordinal)
            .Select(g => new LabelRow(g.Key, g.First().ExpectOk, g.Count(), g.Count(i => i.Correct)))
            .OrderBy(l => !l.ExpectOk).ThenBy(l => l.Label, StringComparer.Ordinal)   // 양품 먼저
            .ToList();
        var correct = items.Count(i => i.Correct);
        return new TestReport(true, $"{correct}/{items.Count} 맞음", labels, items, correct, items.Count);
    }

    private static async Task<TestItem> RunOne(VisionJob job, string file, string label, bool expectOk, CancellationToken ct)
    {
        VisionImage img;
        try
        {
            img = VisionImage.Load(file);
        }
        catch (Exception ex)
        {
            return new TestItem(file, label, expectOk, "Error", [], $"이미지를 읽을 수 없습니다: {ex.Message}", false);
        }
        using (img)
        {
            using var r = await job.RunAsync(img, ct).ConfigureAwait(false);
            var state = r.State.ToString();
            // Error 는 기대가 NG 여도 정답이 아니다 (검사를 못 한 것) — 틀린 목록에 사유와 함께 나오게
            var correct = r.State != VisionState.Error && expectOk == (r.State == VisionState.Ok);
            return new TestItem(file, label, expectOk, state, [.. r.Codes], r.Error, correct);
        }
    }
}
