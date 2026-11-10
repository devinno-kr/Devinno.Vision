using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio.Ui;

/// <summary>표의 필터.</summary>
public enum TestFilter
{
    All,
    Wrong,
    Ng,
    Ok,
}

/// <summary>
/// 테스트 화면 — 테스트 폴더를 고르면 이미지 목록이 바로 뜬다 (실행 전에도). 한 장씩 골라 오른쪽에서 지금 로직의 결과(오버레이)를 보고,
/// [이전]/[다음] · ← → 로 넘긴다. [실행] 뒤에는 항목마다 결과 · 맞음/틀림 · 라벨, 필터 [전체/틀린 것/NG/OK].
/// </summary>
public sealed partial class TestPage : DvPage
{
    private readonly StudioSession _session;
    private readonly IFileDialogs _dialogs;
    private readonly EditPage _edit;
    private readonly StatusBar _status;
    private readonly object _gate = new();
    private int _selectedSet;
    private TestFilter _filter;
    private string? _selectedFile;
    private string? _failedCheckId;
    private (int Set, string Folder, TestReport? Report, TestFilter Filter, long Stamp) _rowsKey = (-1, "", null, TestFilter.All, -1);
    private IReadOnlyList<ImageRow> _rows = [];
    private IReadOnlyList<TestImage>? _images;
    private long _imagesStamp;

    public TestPage(StudioSession session, IFileDialogs dialogs, EditPage edit, StatusBar status)
    {
        _session = session;
        _dialogs = dialogs;
        _edit = edit;
        _status = status;
    }

    /// <summary>고른 이미지의 결과.</summary>
    public DvVisionView View { get; } = new();

    // ── 테스트 폴더 ──────────────────────────────────────

    /// <summary>폴더 고르기 항목.</summary>
    public sealed record SetRow(int Index, string Text);

    public IReadOnlyList<SetRow> Sets => [.. _session.TestSets.Select((t, i) => new SetRow(i, $"{Path.GetFileName(t.Folder.TrimEnd('\\', '/'))}  ·  {(t.Rule == LabelRule.FileName ? "파일 이름 라벨" : "하위 폴더 라벨")}"))];

    public int SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (_selectedSet == value)
                return;
            _selectedSet = value;
            ReloadImages();
            Select(null);
        }
    }

    public bool HasProject => _session.Project is not null;
    public bool NoProject => !HasProject;
    public bool HasSets => _session.TestSets.Count > 0;
    public bool NoSets => !HasSets;
    public string SetFolder => _session.TestSets.ElementAtOrDefault(_selectedSet)?.Folder ?? "";
    public bool Running { get; private set; }
    public bool CannotRun => !HasSets || Running || _session.Checks.Count == 0 || _session.BaseImageFile is null;
    public string Progress { get; private set; } = "";

    /// <summary>다음 할 일 / 비활성 이유.</summary>
    public string Hint
    {
        get
        {
            if (NoProject) return "다음: 위 [새 로직] 또는 [열기]";
            if (NoSets) return "다음: [폴더 추가] 로 테스트 이미지 폴더를 고르세요";
            if (_session.Checks.Count == 0) return "검사가 없어 실행할 수 없습니다 — [편집] 에서 검사를 더하세요";
            if (_session.BaseImageFile is null) return "기준 이미지가 없어 실행할 수 없습니다 — [편집] 에서 [기준 이미지 열기]";
            if (Report is null) return "이미지를 골라 한 장씩 보거나, [실행] 으로 폴더 전체를 판정하세요";
            return "필터로 좁히고 한 장씩 보세요 (← → 키로 넘김). 틀린 이미지는 [이 검사 고치기]";
        }
    }

    // ── 보고서 (고른 폴더의 것만) ─────────────────────────

    private TestReport? Report => _session.LastReport is { Ok: true } r && string.Equals(r.Folder, SetFolder, StringComparison.OrdinalIgnoreCase) ? r : null;
    public bool HasReport => Report is not null;
    public bool NoReport => !HasReport;

    /// <summary>종류별 표 한 줄 (표시용).</summary>
    public sealed record LabelLine(string Label, string Expect, int Count, int Correct, string Rate);

    public IReadOnlyList<LabelLine> Labels => Report is { } r
        ? [.. r.Labels.Select(l => new LabelLine(l.Label, l.ExpectOk ? "OK" : "NG", l.Count, l.Correct, $"{100.0 * l.Correct / l.Count:0.#} %"))]
        : [];

    public string Summary => Report is { } r ? r.Message : (HasSets ? $"이미지 {Images.Count}장 · 아직 실행하지 않았습니다" : "테스트 폴더가 없습니다");

    public TestFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            if (_selectedFile is not null && Rows.All(r => r.File != _selectedFile))
                Select(Rows.FirstOrDefault()?.File);   // 필터 밖이면 첫 항목
        }
    }

    // ── 이미지 표 ────────────────────────────────────────

    /// <summary>표 한 줄. Result · Judge 는 실행 전에는 "—".</summary>
    public sealed record ImageRow(int No, string File, string Name, string Label, string Result, string Judge, string? JudgeClass, string? ResultClass);

    private IReadOnlyList<TestImage> Images
    {
        get
        {
            lock (_gate)
                return _images ??= _session.TestImages(_selectedSet);
        }
    }

    /// <summary>표에 보이는 줄 (필터 적용).</summary>
    public IReadOnlyList<ImageRow> Rows
    {
        get
        {
            lock (_gate)
            {
                var key = (_selectedSet, SetFolder, Report, _filter, _imagesStamp);
                if (key == _rowsKey)
                    return _rows;
                var items = Report?.Items.ToDictionary(i => i.File, StringComparer.OrdinalIgnoreCase);
                var rows = new List<ImageRow>();
                var no = 0;
                foreach (var img in Images)
                {
                    no++;
                    TestItem? it = null;
                    items?.TryGetValue(img.File, out it);
                    var row = new ImageRow(no, img.File, Path.GetFileName(img.File), img.Label ?? "라벨 없음",
                        it?.State switch { "Ok" => "OK", "Ng" => "NG", "Error" => "Error", _ => "—" },
                        it is null ? "—" : it.Correct switch { true => "맞음", false => "틀림", null => "제외" },
                        it?.Correct switch { true => "good", false => "bad", _ => null },
                        it?.State switch { "Ng" => "ng", "Error" => "bad", _ => null });
                    if (Keep(it))
                        rows.Add(row);
                }
                _rowsKey = key;
                return _rows = rows;
            }
        }
    }

    private bool Keep(TestItem? it) => _filter switch
    {
        TestFilter.Wrong => it?.Correct == false,
        TestFilter.Ng => it?.State == "Ng",
        TestFilter.Ok => it?.State == "Ok",
        _ => true,
    };

    public bool HasRows => Rows.Count > 0;
    public bool NoRows => !NoSets && Rows.Count == 0;
    public string NoRowsText => Images.Count == 0 ? "이 폴더에 이미지가 없습니다 (png · bmp · jpg · tif)" : "이 필터에 맞는 이미지가 없습니다";

    /// <summary>표의 선택 (파일로 기억 — 실행 · 필터가 바뀌어도 같은 이미지).</summary>
    public ImageRow? SelectedRow
    {
        get => Rows.FirstOrDefault(r => r.File == _selectedFile);
        set
        {
            if (value?.File != _selectedFile)
                Select(value?.File);
        }
    }

    // ── 고른 이미지 ──────────────────────────────────────

    public bool HasSelection => _selectedFile is not null;
    public bool NoSelection => !HasSelection;
    public string Position => SelectedRow is { } r ? $"{IndexOf(r) + 1} / {Rows.Count}" : $"— / {Rows.Count}";
    public bool CannotPrev => SelectedRow is not { } r || IndexOf(r) <= 0;
    public bool CannotNext => Rows.Count == 0 || (SelectedRow is { } r && IndexOf(r) >= Rows.Count - 1);
    public string Detail { get; private set; } = "";
    public bool CannotFix => _failedCheckId is null;

    /// <summary>고른 이미지를 다시 돌려 보여 주는 중인 작업 (테스트가 기다린다).</summary>
    internal Task Showing { get; private set; } = Task.CompletedTask;

    private int IndexOf(ImageRow r) => Rows.ToList().IndexOf(r);

    public void Prev() => Step(-1);

    public void Next() => Step(+1);

    private void Step(int delta)
    {
        var rows = Rows;
        if (rows.Count == 0)
            return;
        var i = SelectedRow is { } r ? IndexOf(r) + delta : 0;
        if (i < 0 || i >= rows.Count)
            return;
        Select(rows[i].File);
    }

    private void Select(string? file)
    {
        _selectedFile = file;
        _failedCheckId = null;
        if (file is null)
        {
            View.Clear();
            Detail = "";
            return;
        }
        Showing = ShowAsync(file);
    }

    private async Task ShowAsync(string file)
    {
        try
        {
            var name = Path.GetFileName(file);
            var item = Report?.Items.FirstOrDefault(i => string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase));
            var label = Images.FirstOrDefault(i => i.File == file)?.Label;
            var head = $"{name}  ·  라벨 {label ?? "없음"}";
            if (_session.Checks.Count == 0 || _session.BaseImageFile is null)
            {
                // 로직이 없으면 이미지만
                using var img = VisionImage.Load(file);
                if (_selectedFile == file)
                {
                    View.Show(img);
                    Detail = $"{head}  ·  (로직이 없어 이미지만 봅니다)";
                }
                return;
            }
            string? failed = null;
            var r = await _session.RunImageAsync(file, result =>
            {
                if (_selectedFile != file)
                    return;   // 그새 다른 이미지를 골랐다
                View.Show(result);
                failed = result.Steps.FirstOrDefault(s => s.State is StepState.Ng or StepState.Error)?.Name;
            });
            if (_selectedFile != file)
                return;
            _failedCheckId = failed is null ? null : _session.Checks.FirstOrDefault(c => c.Name == failed)?.Id;
            var judge = item?.Correct switch { true => "  ·  맞음", false => "  ·  틀림", _ => "" };
            Detail = $"{head}  ·  {r.Message}{judge}" + (failed is null ? "" : $"  ·  원인 검사: {failed}");
        }
        catch (Exception ex)
        {
            if (_selectedFile == file)
            {
                View.Clear();
                Detail = $"{Path.GetFileName(file)}  ·  열 수 없습니다: {ex.Message}";
            }
        }
        finally
        {
            Invalidate();
        }
    }

    internal SkiaSharp.SKRect AddSetBounds => btnAddSet.Bounds;
    internal SkiaSharp.SKRect RemoveSetBounds => btnRemoveSet.Bounds;
    internal SkiaSharp.SKRect RunBounds => btnRun.Bounds;
    internal SkiaSharp.SKRect NextBounds => btnNext.Bounds;
    internal SkiaSharp.SKRect FixBounds => btnFix.Bounds;

    // ── 명령 ─────────────────────────────────────────────

    /// <summary>폴더 고르기 → 라벨 읽는 법 고르기 → 추가 → 그 폴더의 이미지 목록이 바로 뜬다.</summary>
    public async void AddSet()
    {
        try
        {
            if (_dialogs.PickFolder() is not { } folder)
            {
                _status.Ok("폴더 추가: 취소함");
                return;
            }
            string[] rules = ["파일 이름으로  (02_missinghole.bmp → missinghole, 'good' = 양품)", "하위 폴더 이름으로  (good/, crack/ …, 'good' = 양품)"];
            if (await App.SelectBox("라벨 읽는 법", rules, 0) is not { } pick)
            {
                _status.Ok("폴더 추가: 취소함");
                return;
            }
            var r = _session.AddTestSet(folder, pick == 0 ? LabelRule.FileName : LabelRule.Folder);
            _status.Report(r);
            if (r.Ok)
            {
                _selectedSet = _session.TestSets.Count - 1;
                ReloadImages();
                Select(null);
            }
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    /// <summary>짧게 누르면 확인 창 → [예] 일 때만 삭제.</summary>
    public async void RemoveSet()
    {
        try
        {
            if (_session.TestSets.ElementAtOrDefault(_selectedSet) is not { } set)
                return;
            if (await App.MessageBox($"정말 삭제할까요?\n\n{set.Folder}\n\n(목록에서만 빼고, 폴더와 이미지는 지우지 않습니다)", "테스트 폴더 삭제", DvMessageButtons.YesNo) != DvDialogResult.Yes)
            {
                _status.Ok("삭제 취소함");
                return;
            }
            _status.Report(_session.RemoveTestSet(_selectedSet));
            _selectedSet = 0;
            ReloadImages();
            Select(null);
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    public async void Run()
    {
        try { await RunAsync(); }
        catch (Exception ex) { _status.Fail(ex.Message); }
    }

    /// <summary>고른 폴더를 모두 돌린다 (테스트가 기다릴 수 있게 Task).</summary>
    public async Task RunAsync()
    {
        if (Running)
            return;
        Running = true;
        ReloadImages();   // 폴더 내용이 바뀌었을 수 있다
        var total = Images.Count;
        Progress = $"0 / {total}";
        Invalidate();
        try
        {
            var progress = new Progress<int>(n => { Progress = $"{n} / {total}"; Invalidate(); });
            var r = await _session.RunTestAsync(_selectedSet, progress);
            if (r.Ok) _status.Ok($"테스트: {r.Message}");
            else _status.Fail(r.Message);
        }
        finally
        {
            Running = false;
            Progress = "";
            if (_selectedFile is { } f)
                Select(f);   // 고른 이미지의 판정을 새로
            Invalidate();
        }
    }

    /// <summary>고른 이미지의 원인 검사로 편집 화면에서 고친다.</summary>
    public void Fix()
    {
        if (_failedCheckId is not { } id)
            return;
        _edit.SelectedId = id;
        App.Show(_edit);
        _status.Ok($"편집: {_session.GetCheck(id)?.Name} — 값을 고치고 [테스트] 에서 다시 실행하세요");
    }

    private void ReloadImages()
    {
        lock (_gate)
        {
            _images = null;
            _imagesStamp++;
        }
    }

    private void Invalidate()
    {
        try { App.Invalidate(); }
        catch (InvalidOperationException) { }   // 아직 앱에 붙기 전
    }
}
