using System.Text.Json;
using System.Text.Json.Nodes;
using Devinno.Vision.Recipes;

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
public sealed record RunResult(bool Ok, string Message, string State, IReadOnlyList<string> Codes, IReadOnlyList<ValueRow> Values, byte[]? Png, double Ms = 0);

/// <summary>판정 값 한 줄.</summary>
public sealed record ValueRow(string Check, string Name, double Value, double? Min, double? Max, double? Nominal, string? Unit, bool Ok);

/// <summary>
/// 명령 층 — 화면과 MCP 가 부르는 모든 기능의 단일 입구. 모든 명령은 내부 잠금으로 직렬화되고,
/// 프로젝트를 바꾼 명령은 끝난 뒤(잠금 밖에서) <see cref="ProjectChanged"/> 를 올린다.
/// </summary>
public sealed partial class StudioSession
{
    internal const string NoProject = "프로젝트를 먼저 만들거나 여세요";
    internal const string NoBaseImage = "기준 이미지를 먼저 지정하세요 (set_base_image)";

    private readonly object _gate = new();
    private VisionRecipe? _project;
    private TestReport? _lastReport;

    /// <summary>열린 프로젝트. 화면이 읽기용으로 쓴다 — 바꾸는 것은 반드시 명령으로.</summary>
    public VisionRecipe? Project { get { lock (_gate) return _project; } }

    /// <summary>지금 검사 목록 (복사본 — 화면이 열거하는 동안 MCP 가 바꿔도 안전).</summary>
    public IReadOnlyList<CheckDef> Checks { get { lock (_gate) return _project?.Checks.ToArray() ?? []; } }

    /// <summary>지금 테스트 폴더 목록 (복사본).</summary>
    public IReadOnlyList<TestSet> TestSets { get { lock (_gate) return _project?.TestSets.ToArray() ?? []; } }

    /// <summary>기준 이미지 파일의 전체 경로. 없으면 null.</summary>
    public string? BaseImageFile { get { lock (_gate) return _project?.BaseImagePath; } }

    /// <summary>프로젝트가 바뀐 뒤 (어느 스레드에서든 올 수 있다).</summary>
    public event Action? ProjectChanged;

    // ── 프로젝트 ─────────────────────────────────────────

    /// <summary>새 로직을 만든다. 저장 안 된 변경이 있으면 거부한다 — <paramref name="discardChanges"/> 가 true 일 때만 버린다.</summary>
    public CommandResult NewProject(string folder, string name, bool discardChanges = false)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return CommandResult.Fail("폴더를 지정하세요");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("로직 이름을 지정하세요");
        return Change(() =>
        {
            if (Unsaved(discardChanges) is { } refuse)
                return refuse;
            VisionRecipe p;
            try
            {
                p = VisionRecipe.Create(folder, name.Trim());
                if (File.Exists(Path.Combine(p.Folder, VisionRecipe.FileName)))
                    return CommandResult.Fail($"이 폴더에는 이미 프로젝트가 있습니다: {p.Folder} (open_project 로 여세요)");
                p.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return CommandResult.Fail($"이 폴더에 프로젝트를 만들 수 없습니다: {ex.Message}");
            }
            Replace(p);
            return CommandResult.Done($"새 프로젝트: {p.Name} ({p.Folder})");
        }, needsProject: false, markDirty: false);
    }

    /// <summary>저장된 로직을 연다. 저장 안 된 변경이 있으면 거부한다 — <paramref name="discardChanges"/> 가 true 일 때만 버린다.</summary>
    public CommandResult OpenProject(string folder, bool discardChanges = false)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return CommandResult.Fail("폴더를 지정하세요");
        return Change(() =>
        {
            if (Unsaved(discardChanges) is { } refuse)
                return refuse;
            VisionRecipe p;
            try
            {
                p = VisionRecipe.Load(folder, loadModels: false);   // 모델은 실행할 때 읽는다 (학습 전 · 파일 없음이어도 열리게)
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return CommandResult.Fail($"열 수 없습니다: {ex.Message}");   // 지금 열린 프로젝트는 그대로
            }
            var message = $"열림: {p.Name} (검사 {p.Checks.Count}개{(p.GoodModel is null ? "" : ", 양품 모델")})";
            Replace(p);
            return CommandResult.Done(message);
        }, needsProject: false, markDirty: false);
    }

    /// <summary>저장 안 된 변경이 있는가 (편집 명령 뒤 true, 저장 · 새로 만들기 · 열기 뒤 false). 실행 · 테스트는 바꾸지 않는다.</summary>
    public bool IsDirty { get { lock (_gate) return _dirty; } }

    // 잠금 안에서 부른다
    private CommandResult? Unsaved(bool discard) => _dirty && !discard && _project is { } cur
        ? CommandResult.Fail($"저장하지 않은 변경이 있습니다: {cur.Name} — 먼저 저장하거나(save_project), 변경을 버리고 진행하세요 (discard_changes)")
        : null;

    public CommandResult SaveProject()
    {
        CommandResult r;
        lock (_gate)
        {
            if (_project is not { } p)
                return CommandResult.Fail(NoProject);
            if (_training is not null)
                return CommandResult.Fail(Training);
            try
            {
                p.Save();
                _dirty = false;
                r = CommandResult.Done($"저장됨: {Path.Combine(p.Folder, VisionRecipe.FileName)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CommandResult.Fail($"저장할 수 없습니다: {ex.Message}");
            }
        }
        ProjectChanged?.Invoke();   // 화면이 "저장 안 됨" 을 지우게
        return r;
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(imagePath, dest, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandResult.Fail($"기준 이미지를 프로젝트에 복사할 수 없습니다: {ex.Message}");
        }
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

    /// <summary>
    /// 저장된 로직을 레시피 패키지(.dvpkg) 하나로 묶는다 — 현장 앱은 VisionRecipe.Load(파일) 로 연다.
    /// 테스트 폴더 · 양품/불량 폴더 · 학습 요약 같은 Studio 전용 정보는 넣지 않는다.
    /// </summary>
    public CommandResult PackProject(string file)
    {
        lock (_gate)
        {
            if (_project is not { } p)
                return CommandResult.Fail(NoProject);
            if (_training is not null)
                return CommandResult.Fail(Training);
            if (_dirty)
                return CommandResult.Fail("저장하지 않은 변경이 있습니다 — 먼저 저장하세요 (save_project). 패키지는 저장된 로직을 묶습니다");
            if (string.IsNullOrWhiteSpace(file))
                return CommandResult.Fail("패키지 파일 경로를 주세요 (.dvpkg)");
            var full = Path.GetFullPath(file);
            if (Overlaps(Path.GetDirectoryName(full)!, p.Folder) && full.StartsWith(Path.TrimEndingDirectorySeparator(p.Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"패키지를 프로젝트 폴더 안에 만들 수 없습니다: {full}");
            try
            {
                var n = p.Pack(full);
                var size = new FileInfo(full).Length;
                return CommandResult.Done($"패키지: {full} (파일 {n}개, {size / 1024.0 / 1024.0:0.#} MB) — 현장 앱: VisionRecipe.Load(\"{Path.GetFileName(full)}\")");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                return CommandResult.Fail($"패키지를 만들 수 없습니다: {ex.Message}");
            }
        }
    }

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
        var reason = p.GoodModel is not null && check is not BlobAlignCheck and not LengthCheck;
        if (reason)
            check.Role = StepRole.Reason;
        return CommandResult.Done($"추가됨: {name} ({check.KindLabel}, id {check.Id}){(reason ? " — 사유 검사 (양품 모델이 판정하고, 이 검사는 NG 이유를 붙입니다)" : "")}", check.Id);
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
        var current = JsonSerializer.SerializeToNode(c, VisionRecipe.JsonOptions)!.AsObject();
        current.TryAdd("Role", nameof(StepRole.Judge));   // 기본값은 쓰지 않으므로 채워 둔다
        if (Merge(current, properties, "") is { } error)
            return CommandResult.Fail(error);
        CheckDef updated;
        try
        {
            updated = current.Deserialize<CheckDef>(VisionRecipe.JsonOptions)!;
        }
        catch (JsonException ex)
        {
            return CommandResult.Fail($"속성 값이 맞지 않습니다: {ex.Message}");
        }
        updated.Name = updated.Name?.Trim() ?? "";
        if (updated.Name.Length == 0)
            return CommandResult.Fail("검사 이름은 비울 수 없습니다");
        if (updated.Validate() is { } problem)
            return CommandResult.Fail(problem);
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
            return _project is { } p ? JsonSerializer.Serialize(p, VisionRecipe.JsonOptions) : null;
    }

    // ── 티칭 · 실행 ───────────────────────────────────────

    /// <summary>기준 이미지로 정렬 검사를 돌려 그 위치를 기준(Reference)으로 저장한다.</summary>
    public CommandResult TeachBase() => Change(() =>
    {
        var p = _project!;
        if (p.BaseImagePath is not { } path)
            return CommandResult.Fail(NoBaseImage);
        if (p.Checks.OfType<BlobAlignCheck>().FirstOrDefault() is not { } align)
            return CommandResult.Fail("정렬 검사(BlobAlign)가 없어 티칭할 것이 없습니다");
        var probe = new BlobAlignCheck { Name = align.Name, Roi = align.Roi, Blob = align.Blob };   // 기준 없이 = 지금 위치 그대로
        var job = new VisionJob("teach").Add(probe.CreateStep(p), roi: align.Roi.IsEmpty ? null : () => probe.Roi, pose: VisionRecipe.PoseName);
        VisionImage img;
        try
        {
            img = VisionImage.Load(path);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"기준 이미지를 읽을 수 없습니다: {ex.Message} (set_base_image 로 다시 지정하세요)");
        }
        using var loaded = img;
        using var r = job.RunAsync(img).GetAwaiter().GetResult();
        if (!r.Poses.TryGetValue(VisionRecipe.PoseName, out var pose))
            return CommandResult.Fail($"기준 이미지에서 부품을 찾지 못했습니다: {r.Error} (정렬 검사의 Blob 설정을 확인하세요)");
        align.Reference = pose;
        var stale = p.GoodModel is not null && !p.GoodModelIsCurrent ? " — 양품 모델을 다시 학습하세요 (기준 위치가 바뀌었습니다)" : "";
        return CommandResult.Done($"기준 위치: x {pose.X:0.0}, y {pose.Y:0.0}, 각도 {pose.Angle:0.00}°{stale}");
    });

    /// <summary>이미지 한 장을 지금 로직으로 실행한다. 결과 PNG 를 담는다.</summary>
    public Task<RunResult> RunImageAsync(string imagePath) => RunCoreAsync(imagePath, see: null);

    /// <summary>화면용 — 결과를 해제되기 전에 <paramref name="see"/> 에 넘긴다 (PNG 는 만들지 않는다).</summary>
    internal Task<RunResult> RunImageAsync(string imagePath, Action<VisionResult> see) => RunCoreAsync(imagePath, see);

    private async Task<RunResult> RunCoreAsync(string imagePath, Action<VisionResult>? see)
    {
        VisionJob job;
        lock (_gate)
        {
            if (_project is not { } p)
                return Failed(NoProject);
            if (_training is not null)
                return Failed(Training);
            if (TryCreateJob(p, out job) is { } error)
                return Failed(error);
            job.ReasonSteps = ReasonRun.Always;   // 사유 검사의 값도 보이게 (판정은 그대로)
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
            see?.Invoke(r);
            return ToRunResult(r, see is null ? ResultRenderer.Render(r) : null);
        }
    }

    /// <summary>마지막 보고서의 항목 하나를 지금 로직으로 다시 돌려 결과 PNG 를 만든다. 보고서가 없거나 번호가 범위 밖이면 null.</summary>
    public async Task<byte[]?> RenderTestItemAsync(int index)
    {
        if (TestItemFile(index) is not { } file)
            return null;
        var r = await RunImageAsync(file).ConfigureAwait(false);
        return r.Png;
    }

    /// <summary>테스트 폴더의 이미지 목록과 라벨 (실행 전에도). 폴더를 읽을 수 없으면 빈 목록.</summary>
    public IReadOnlyList<TestImage> TestImages(int setIndex)
    {
        TestSet set;
        string? baseImage;
        lock (_gate)
        {
            if (_project is not { } p || setIndex < 0 || setIndex >= p.TestSets.Count)
                return [];
            set = p.TestSets[setIndex];
            baseImage = p.BaseImagePath;
        }
        try
        {
            return Directory.Exists(set.Folder) ? TestRunner.Collect(set, baseImage) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // 이미지 폴더로 쓸 수 없는 곳: 프로젝트 폴더 자체 · 그 위 폴더 (studio.json · 모델이 섞인다) · models 폴더. 프로젝트 안의 다른 하위 폴더는 된다
    // (2026-11-11 사용자 프로젝트: images/ 에 테스트 이미지, train_good/ 에 양품). 잠금 안에서. 문제가 없으면 null
    private string? NotAnImageFolder(string full, string what)
    {
        var project = _project!.Folder;
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        if (Norm(project).StartsWith(Norm(full), StringComparison.OrdinalIgnoreCase))
            return $"프로젝트 폴더나 그 위 폴더는 {what}로 쓸 수 없습니다 (studio.json · 모델이 섞입니다): {full}";
        if (Norm(full).StartsWith(Norm(Path.Combine(project, "models")), StringComparison.OrdinalIgnoreCase))
            return $"models 폴더는 {what}로 쓸 수 없습니다: {full}";
        return null;
    }

    // 두 폴더가 같거나 한쪽이 다른 쪽 안에 있는가
    private static bool Overlaps(string a, string b)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var (x, y) = (Norm(a), Norm(b));
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>마지막 보고서 항목의 파일. 없으면 null.</summary>
    internal string? TestItemFile(int index)
    {
        lock (_gate)
            return _lastReport is { } rep && index >= 0 && index < rep.Items.Count ? rep.Items[index].File : null;
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
        if (NotAnImageFolder(full, "테스트 폴더") is { } refuse)
            return CommandResult.Fail(refuse);
        if (sets.Any(t => string.Equals(Path.GetFullPath(t.Folder), full, StringComparison.OrdinalIgnoreCase) && t.Rule == rule))
            return CommandResult.Fail($"이미 있는 테스트 폴더입니다: {full}");
        sets.Add(new TestSet { Folder = full, Rule = rule });
        return CommandResult.Done($"테스트 폴더 {sets.Count - 1}: {full} (라벨 규칙 {rule}){OverlapWarning(full, _goods.Folder, "양품 폴더")}");
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
        string? baseImage;
        TestSet set;
        lock (_gate)
        {
            if (_project is not { } p)
                return TestRunner.Fail(NoProject);
            if (_training is not null)
                return TestRunner.Fail(Training);
            if (p.BaseImagePath is null && p.GoodModel is null)
                return TestRunner.Fail(NoBaseImage);
            if (setIndex < 0 || setIndex >= p.TestSets.Count)
                return TestRunner.Fail(p.TestSets.Count == 0 ? "테스트 폴더가 없습니다 (add_test_set)" : $"테스트 폴더 번호는 0 ~ {p.TestSets.Count - 1} 입니다 (받은 값 {setIndex})");
            if (TryCreateJob(p, out job) is { } error)
                return TestRunner.Fail(error);
            baseImage = p.BaseImagePath;
            var s = p.TestSets[setIndex];
            set = new TestSet { Folder = s.Folder, Rule = s.Rule, GoodLabel = s.GoodLabel };
        }
        var report = await TestRunner.RunAsync(job, set, progress, ct, baseImage).ConfigureAwait(false);   // 프로젝트 안 폴더면 기준 이미지 사본은 뺀다
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
        return new RunResult(true, msg, r.State.ToString(), [.. r.Codes], values, png, r.Elapsed.TotalMilliseconds);
    }

    private static RunResult Failed(string message) => new(false, message, "Error", [], [], null);

    // ── 내부 ─────────────────────────────────────────────

    // 잠금 안에서 바꾸고, 성공했으면 잠금 밖에서 알린다
    private CommandResult Change(Func<CommandResult> change, bool needsProject = true, bool markDirty = true)
    {
        CommandResult r;
        lock (_gate)
        {
            if (_training is not null)
                return CommandResult.Fail(Training);
            if (needsProject && _project is null)
                return CommandResult.Fail(NoProject);
            r = change();
            if (r.Ok)
            {
                _revision++;
                if (markDirty)
                    _dirty = true;
            }
        }
        if (r.Ok)
            ProjectChanged?.Invoke();
        return r;
    }

    private long _revision;
    private bool _dirty;

    /// <summary>로직이 바뀔 때마다 1 씩 는다 (보여 주던 결과가 낡았는지 화면이 알 수 있게). 보고서만 바뀐 것은 세지 않는다.</summary>
    public long Revision { get { lock (_gate) return _revision; } }

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
            if (value is null && target[existing] is JsonObject)
                return $"{path}{existing} 은 비울 수 없습니다 (바꿀 값만 객체로 주세요)";
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
