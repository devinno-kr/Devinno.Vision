using System.Text.Json;
using System.Text.Json.Nodes;
using Devinno.Vision.Studio.Model;

namespace Devinno.Vision.Studio.Session;

/// <summary>명령 하나의 결과. 실패는 예외 대신 <c>Ok=false</c> + 고칠 방법을 담은 문구.</summary>
/// <param name="Ok">성공 여부.</param>
/// <param name="Message">사람(과 AI)이 읽을 문구.</param>
/// <param name="Id">새로 만든 검사의 id (AddCheck).</param>
public sealed record CommandResult(bool Ok, string Message, string? Id = null)
{
    internal static CommandResult Done(string message, string? id = null) => new(true, message, id);
    internal static CommandResult Fail(string message) => new(false, message);
}

/// <summary>한 장 실행 결과.</summary>
public sealed record RunResult(bool Ok, string Message, string State, IReadOnlyList<string> Codes, IReadOnlyList<ValueRow> Values, byte[]? Png);

/// <summary>판정 값 한 줄.</summary>
public sealed record ValueRow(string Check, string Name, double Value, double? Min, double? Max, double? Nominal, string? Unit, bool Ok);

/// <summary>
/// 명령 층 — 화면과 MCP 가 부르는 모든 기능의 단일 입구. 모든 명령은 내부 잠금으로 직렬화되고,
/// 프로젝트를 바꾼 명령은 끝난 뒤(잠금 밖에서) <see cref="ProjectChanged"/> 를 올린다.
/// </summary>
public sealed class StudioSession
{
    internal const string NoProject = "프로젝트를 먼저 만들거나 여세요";
    internal const string NoBaseImage = "기준 이미지를 먼저 지정하세요 (set_base_image)";

    private readonly object _gate = new();
    private StudioProject? _project;
    private TestReport? _lastReport;

    /// <summary>열린 프로젝트. 화면이 읽기용으로 쓴다 — 바꾸는 것은 반드시 명령으로.</summary>
    public StudioProject? Project { get { lock (_gate) return _project; } }

    /// <summary>프로젝트가 바뀐 뒤 (어느 스레드에서든 올 수 있다).</summary>
    public event Action? ProjectChanged;

    // ── 프로젝트 ─────────────────────────────────────────

    public CommandResult NewProject(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return CommandResult.Fail("폴더를 지정하세요");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("로직 이름을 지정하세요");
        return Change(() =>
        {
            var p = StudioProject.Create(folder, name.Trim());
            if (File.Exists(Path.Combine(p.Folder, StudioProject.FileName)))
                return CommandResult.Fail($"이 폴더에는 이미 프로젝트가 있습니다: {p.Folder} (open_project 로 여세요)");
            p.Save();
            _project = p;
            _lastReport = null;
            return CommandResult.Done($"새 프로젝트: {p.Name} ({p.Folder})");
        }, needsProject: false);
    }

    public CommandResult OpenProject(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return CommandResult.Fail("폴더를 지정하세요");
        return Change(() =>
        {
            StudioProject p;
            try
            {
                p = StudioProject.Load(folder);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return CommandResult.Fail($"열 수 없습니다: {ex.Message}");   // 지금 열린 프로젝트는 그대로
            }
            _project = p;
            _lastReport = null;
            return CommandResult.Done($"열림: {p.Name} (검사 {p.Checks.Count}개)");
        }, needsProject: false);
    }

    public CommandResult SaveProject()
    {
        lock (_gate)
        {
            if (_project is not { } p)
                return CommandResult.Fail(NoProject);
            try
            {
                p.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CommandResult.Fail($"저장할 수 없습니다: {ex.Message}");
            }
            return CommandResult.Done($"저장됨: {Path.Combine(p.Folder, StudioProject.FileName)}");
        }
    }

    public CommandResult SetBaseImage(string imagePath) => Change(() =>
    {
        var p = _project!;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return CommandResult.Fail($"이미지 파일이 없습니다: {imagePath}");
        try
        {
            using var img = VisionImage.Load(imagePath);   // 읽히는지 확인
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"이미지를 읽을 수 없습니다: {ex.Message}");
        }
        var rel = Path.Combine("images", "base" + Path.GetExtension(imagePath).ToLowerInvariant()).Replace('\\', '/');
        var dest = Path.Combine(p.Folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(imagePath, dest, overwrite: true);
        p.BaseImage = rel;
        return CommandResult.Done($"기준 이미지: {rel}");
    });

    public CommandResult SetCalibration(double? mmPerPx) => Change(() =>
    {
        if (mmPerPx is { } v && (!(v > 0) || double.IsInfinity(v)))
            return CommandResult.Fail($"mm/px 는 0 보다 큰 수여야 합니다 (받은 값 {v})");
        _project!.Calibration = mmPerPx is { } m ? VisionCalibration.Scale(m) : null;
        return CommandResult.Done(mmPerPx is { } x ? $"캘리브레이션: {x} mm/px (길이 단위 mm)" : "캘리브레이션 없음 (길이 단위 px)");
    });

    // ── 검사 ─────────────────────────────────────────────

    public CommandResult AddCheck(string kind, string name) => Change(() =>
    {
        var p = _project!;
        if (CheckDef.New(kind ?? "") is not { } check)
            return CommandResult.Fail($"알 수 없는 검사 종류: {kind} (가능: {string.Join(", ", CheckDef.Kinds)})");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("검사 이름을 지정하세요");
        name = name.Trim();
        if (p.Checks.Any(c => c.Name == name))
            return CommandResult.Fail($"같은 이름의 검사가 있습니다: {name}");
        if (check is BlobAlignCheck)
        {
            if (p.Checks.OfType<BlobAlignCheck>().Any())
                return CommandResult.Fail("정렬 검사는 하나만 둘 수 있습니다");
            p.Checks.Insert(0, check);   // 정렬은 항상 맨 앞 (뒤 검사들이 따라간다)
        }
        else
        {
            p.Checks.Add(check);
        }
        check.Name = name;
        while (p.Checks.Count(c => c.Id == check.Id) > 1)
            check.Id = Guid.NewGuid().ToString("N")[..8];
        return CommandResult.Done($"추가됨: {name} ({check.KindLabel}, id {check.Id})", check.Id);
    });

    public CommandResult RemoveCheck(string id) => Change(() =>
    {
        if (Find(id) is not { } c)
            return NotFound(id);
        _project!.Checks.Remove(c);
        return CommandResult.Done($"삭제됨: {c.Name}");
    });

    public CommandResult MoveCheck(string id, int index) => Change(() =>
    {
        var list = _project!.Checks;
        if (Find(id) is not { } c)
            return NotFound(id);
        if (index < 0 || index >= list.Count)
            return CommandResult.Fail($"위치는 0 ~ {list.Count - 1} 이어야 합니다 (받은 값 {index})");
        var hasAlign = list[0] is BlobAlignCheck;
        if (c is BlobAlignCheck && index != 0)
            return CommandResult.Fail("정렬 검사는 맨 앞에 있어야 합니다");
        if (c is not BlobAlignCheck && hasAlign && index == 0)
            return CommandResult.Fail("정렬 검사 앞으로는 옮길 수 없습니다 (위치 1 이상)");
        list.Remove(c);
        list.Insert(index, c);
        return CommandResult.Done($"옮김: {c.Name} → {index}");
    });

    public CommandResult SetRoi(string id, VisionRoi roi) => Change(() =>
    {
        if (Find(id) is not { } c)
            return NotFound(id);
        if (roi.IsEmpty && c is not BlobAlignCheck)
            return CommandResult.Fail("ROI 의 폭과 높이는 0 보다 커야 합니다");
        c.Roi = roi.IsEmpty ? default : roi;
        return CommandResult.Done($"ROI: {c.Name} = {Describe(c.Roi)}");
    });

    /// <summary>이름 · 종류별 속성 일부만 바꾼다 (JSON 병합, 대소문자 무시). Kind · Id 는 바꿀 수 없어 무시한다.</summary>
    public CommandResult UpdateCheck(string id, JsonObject properties) => Change(() =>
    {
        var p = _project!;
        if (Find(id) is not { } c)
            return NotFound(id);
        if (properties is null)
            return CommandResult.Fail("바꿀 속성을 주세요 (JSON 객체)");
        var current = JsonSerializer.SerializeToNode(c, StudioProject.Json)!.AsObject();
        if (Merge(current, properties, "") is { } error)
            return CommandResult.Fail(error);
        CheckDef updated;
        try
        {
            updated = current.Deserialize<CheckDef>(StudioProject.Json)!;
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"속성 값이 맞지 않습니다: {ex.Message}");
        }
        updated.Name = updated.Name?.Trim() ?? "";
        if (updated.Name.Length == 0)
            return CommandResult.Fail("검사 이름은 비울 수 없습니다");
        if (p.Checks.Any(o => o != c && o.Name == updated.Name))
            return CommandResult.Fail($"같은 이름의 검사가 있습니다: {updated.Name}");
        p.Checks[p.Checks.IndexOf(c)] = updated;
        return CommandResult.Done($"바뀜: {updated.Name}");
    });

    public CheckDef? GetCheck(string id)
    {
        lock (_gate)
            return Find(id);
    }

    /// <summary>열린 프로젝트 전체를 studio.json 과 같은 형식으로 (잠금 안에서 찍는다). 없으면 null.</summary>
    public string? ProjectJson()
    {
        lock (_gate)
            return _project is { } p ? JsonSerializer.Serialize(p, StudioProject.Json) : null;
    }

    // ── 티칭 · 실행 ───────────────────────────────────────

    /// <summary>기준 이미지로 정렬 검사를 돌려 그 위치를 기준(Reference)으로 저장한다.</summary>
    public CommandResult TeachBase() => Change(() =>
    {
        var p = _project!;
        if (BaseImagePath(p) is not { } path)
            return CommandResult.Fail(NoBaseImage);
        if (p.Checks.OfType<BlobAlignCheck>().FirstOrDefault() is not { } align)
            return CommandResult.Fail("정렬 검사(BlobAlign)가 없어 티칭할 것이 없습니다");
        var probe = new BlobAlignCheck { Name = align.Name, Roi = align.Roi, Blob = align.Blob };   // 기준 없이 = 지금 위치 그대로
        var job = new VisionJob("teach").Add(probe.Build(p), roi: align.Roi.IsEmpty ? null : () => probe.Roi, pose: JobBuilder.PoseName);
        using var img = VisionImage.Load(path);
        using var r = job.RunAsync(img).GetAwaiter().GetResult();
        if (!r.Poses.TryGetValue(JobBuilder.PoseName, out var pose))
            return CommandResult.Fail($"기준 이미지에서 부품을 찾지 못했습니다: {r.Error} (정렬 검사의 Blob 설정을 확인하세요)");
        align.Reference = pose;
        return CommandResult.Done($"기준 위치: x {pose.X:0.0}, y {pose.Y:0.0}, 각도 {pose.Angle:0.00}°");
    });

    /// <summary>이미지 한 장을 지금 로직으로 실행한다.</summary>
    public async Task<RunResult> RunImageAsync(string imagePath)
    {
        VisionJob job;
        lock (_gate)
        {
            if (_project is not { } p)
                return Failed(NoProject);
            if (p.Checks.Count == 0)
                return Failed("검사가 없습니다 (add_check)");
            job = JobBuilder.Build(p);
        }
        VisionImage img;
        try
        {
            img = VisionImage.Load(imagePath);
        }
        catch (Exception ex)
        {
            return Failed($"이미지를 읽을 수 없습니다: {imagePath} ({ex.Message})");
        }
        using (img)
        {
            using var r = await job.RunAsync(img).ConfigureAwait(false);
            return ToRunResult(r, ResultRenderer.Render(r));
        }
    }

    /// <summary>마지막 보고서의 항목 하나를 지금 로직으로 다시 돌려 결과 PNG 를 만든다. 보고서가 없거나 번호가 범위 밖이면 null.</summary>
    public async Task<byte[]?> RenderTestItemAsync(int index)
    {
        string file;
        lock (_gate)
        {
            if (_lastReport is not { } rep || index < 0 || index >= rep.Items.Count)
                return null;
            file = rep.Items[index].File;
        }
        var r = await RunImageAsync(file).ConfigureAwait(false);
        return r.Png;
    }

    // ── 폴더 테스트 ───────────────────────────────────────

    /// <summary>마지막 폴더 테스트 보고서.</summary>
    public TestReport? LastReport { get { lock (_gate) return _lastReport; } }

    public CommandResult AddTestSet(string folder, LabelRule rule) => Change(() =>
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return CommandResult.Fail($"폴더가 없습니다: {folder}");
        var sets = _project!.TestSets;
        var full = Path.GetFullPath(folder);
        if (sets.Any(t => string.Equals(Path.GetFullPath(t.Folder), full, StringComparison.OrdinalIgnoreCase) && t.Rule == rule))
            return CommandResult.Fail($"이미 있는 테스트 폴더입니다: {full}");
        sets.Add(new TestSet { Folder = full, Rule = rule });
        return CommandResult.Done($"테스트 폴더 {sets.Count - 1}: {full} (라벨 규칙 {rule})");
    });

    public CommandResult RemoveTestSet(int index) => Change(() =>
    {
        var sets = _project!.TestSets;
        if (index < 0 || index >= sets.Count)
            return CommandResult.Fail($"테스트 폴더 번호는 0 ~ {sets.Count - 1} 입니다 (받은 값 {index})");
        sets.RemoveAt(index);
        return CommandResult.Done($"테스트 폴더 {index} 삭제됨");
    });

    /// <summary>테스트 폴더 하나를 지금 로직으로 모두 돌린다. 결과는 <see cref="LastReport"/> 에도 남는다.</summary>
    public async Task<TestReport> RunTestAsync(int setIndex, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        VisionJob job;
        TestSet set;
        lock (_gate)
        {
            if (_project is not { } p)
                return TestRunner.Fail(NoProject);
            if (BaseImagePath(p) is null)
                return TestRunner.Fail(NoBaseImage);
            if (p.Checks.Count == 0)
                return TestRunner.Fail("검사가 없습니다 (add_check)");
            if (setIndex < 0 || setIndex >= p.TestSets.Count)
                return TestRunner.Fail(p.TestSets.Count == 0 ? "테스트 폴더가 없습니다 (add_test_set)" : $"테스트 폴더 번호는 0 ~ {p.TestSets.Count - 1} 입니다 (받은 값 {setIndex})");
            job = JobBuilder.Build(p);
            var s = p.TestSets[setIndex];
            set = new TestSet { Folder = s.Folder, Rule = s.Rule, GoodLabel = s.GoodLabel };
        }
        var report = await TestRunner.RunAsync(job, set, progress, ct).ConfigureAwait(false);
        lock (_gate)
            _lastReport = report;
        ProjectChanged?.Invoke();   // 화면이 보고서를 다시 그리게
        return report;
    }

    internal static RunResult ToRunResult(VisionResult r, byte[]? png)
    {
        var values = r.Values.Select(v => new ValueRow(v.Step, v.Name, v.Value, v.Min, v.Max, v.Nominal, v.Unit, v.Ok)).ToList();
        var msg = r.State switch
        {
            VisionState.Ok => "OK",
            VisionState.Ng => $"NG: {string.Join(", ", r.Codes)}",
            _ => $"Error: {r.Error}",
        };
        return new RunResult(true, msg, r.State.ToString(), [.. r.Codes], values, png);
    }

    private static RunResult Failed(string message) => new(false, message, "Error", [], [], null);

    // ── 내부 ─────────────────────────────────────────────

    internal static string? BaseImagePath(StudioProject p)
    {
        if (string.IsNullOrEmpty(p.BaseImage))
            return null;
        var path = Path.Combine(p.Folder, p.BaseImage);
        return File.Exists(path) ? path : null;
    }

    // 잠금 안에서 바꾸고, 성공했으면 잠금 밖에서 알린다
    private CommandResult Change(Func<CommandResult> change, bool needsProject = true)
    {
        CommandResult r;
        lock (_gate)
        {
            if (needsProject && _project is null)
                return CommandResult.Fail(NoProject);
            r = change();
        }
        if (r.Ok)
            ProjectChanged?.Invoke();
        return r;
    }

    private CheckDef? Find(string id) => _project?.Checks.FirstOrDefault(c => c.Id == id);

    private static CommandResult NotFound(string id) => CommandResult.Fail($"검사를 찾을 수 없습니다: {id} (get_project 로 id 를 확인하세요)");

    private static string Describe(VisionRoi r) => r.IsEmpty ? "전체 이미지" : $"{r.Shape} {r.X:0.#},{r.Y:0.#} {r.Width:0.#}×{r.Height:0.#}" + (r.Shape == VisionRoiShape.Rotated ? $" {r.Angle:0.#}°" : "");

    // 받은 키를 지금 값에 덮는다. 키는 대소문자 무시, 모르는 키는 거부, 객체끼리는 안으로 병합
    private static string? Merge(JsonObject target, JsonObject patch, string path)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (path.Length == 0 && (key.Equals("Kind", StringComparison.OrdinalIgnoreCase) || key.Equals("Id", StringComparison.OrdinalIgnoreCase)))
                continue;
            var existing = target.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return $"알 수 없는 속성: {path}{key} (가능: {string.Join(", ", target.Select(kv => kv.Key).Where(k => k != "Kind" && k != "Id"))})";
            if (value is JsonObject sub && target[existing] is JsonObject cur)
            {
                if (Merge(cur, sub, $"{path}{existing}.") is { } err)
                    return err;
            }
            else
            {
                target[existing] = value?.DeepClone();
            }
        }
        return null;
    }
}
