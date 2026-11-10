using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio.Ui;

/// <summary>테스트 화면 — 테스트 폴더 · 실행 · 종류별 표 · 틀린 이미지 · 선택 항목 결과.</summary>
public sealed partial class TestPage : DvPage
{
    private readonly StudioSession _session;
    private readonly IFileDialogs _dialogs;
    private readonly EditPage _edit;
    private int? _selectedFailure;
    private string? _failedCheckId;

    public TestPage(StudioSession session, IFileDialogs dialogs, EditPage edit)
    {
        _session = session;
        _dialogs = dialogs;
        _edit = edit;
    }

    /// <summary>선택한 틀린 항목의 결과.</summary>
    public DvVisionView View { get; } = new();

    // ── 테스트 폴더 ──────────────────────────────────────

    /// <summary>폴더 목록 한 줄.</summary>
    public sealed record SetRow(int Index, string Text);

    public IReadOnlyList<SetRow> Sets => [.. _session.TestSets.Select((t, i) => new SetRow(i, $"{Path.GetFileName(t.Folder.TrimEnd('\\', '/'))}  ·  {(t.Rule == LabelRule.FileName ? "파일 이름" : "하위 폴더")}"))];

    public int SelectedSet { get; set; }

    public LabelRule NewRule { get; set; } = LabelRule.FileName;

    public bool HasProject => _session.Project is not null;
    public bool NoProject => !HasProject;
    public bool CannotRun => !HasProject || Running || _session.TestSets.Count == 0;
    public bool Running { get; private set; }

    public string Message { get; private set; } = "";
    public bool HasMessage => Message.Length > 0;
    public string Progress { get; private set; } = "";

    // ── 보고서 ───────────────────────────────────────────

    /// <summary>종류별 표 한 줄 (표시용).</summary>
    public sealed record LabelLine(string Label, string Expect, int Count, int Correct, string Rate);

    /// <summary>틀린 항목 한 줄.</summary>
    public sealed record FailureRow(int Index, string Text);

    public IReadOnlyList<LabelLine> Labels => _session.LastReport is { Ok: true } r
        ? [.. r.Labels.Select(l => new LabelLine(l.Label, l.ExpectOk ? "OK" : "NG", l.Count, l.Correct, $"{100.0 * l.Correct / l.Count:0.#} %"))]
        : [];

    public IReadOnlyList<FailureRow> Failures => _session.LastReport is { Ok: true } r
        ? [.. r.Items.Select((i, index) => (i, index)).Where(x => !x.i.Correct)
            .Select(x => new FailureRow(x.index, $"{Path.GetFileName(x.i.File)}  ·  {x.i.Label}  →  {x.i.State}{(x.i.Codes.Count > 0 ? " " + string.Join(",", x.i.Codes) : "")}"))]
        : [];

    public string Summary => _session.LastReport is { } r ? r.Message : "아직 실행하지 않았습니다";

    public int? SelectedFailure
    {
        get => _selectedFailure;
        set
        {
            _selectedFailure = value;
            if (value is { } i)
                Showing = ShowItemAsync(i);
        }
    }

    /// <summary>선택 항목을 다시 돌려 보여 주는 중인 작업 (테스트가 기다린다).</summary>
    internal Task Showing { get; private set; } = Task.CompletedTask;

    public string FailureDetail { get; private set; } = "";
    public bool CannotFix => _failedCheckId is null;

    // ── 명령 ─────────────────────────────────────────────

    public void AddSet()
    {
        if (_dialogs.PickFolder() is not { } folder)
            return;
        var r = _session.AddTestSet(folder, NewRule);
        Message = r.Ok ? "" : r.Message;
        if (r.Ok)
            SelectedSet = _session.TestSets.Count - 1;
    }

    public void RemoveSet()
    {
        var r = _session.RemoveTestSet(SelectedSet);
        Message = r.Ok ? "" : r.Message;
        SelectedSet = 0;
    }

    public async void Run()
    {
        try { await RunAsync(); }
        catch (Exception ex) { Message = $"오류: {ex.Message}"; }
    }

    /// <summary>선택한 폴더를 돌린다 (테스트가 기다릴 수 있게 Task).</summary>
    public async Task RunAsync()
    {
        if (Running)
            return;
        Running = true;
        Progress = "실행 중…";
        _selectedFailure = null;
        _failedCheckId = null;
        FailureDetail = "";
        View.Clear();
        try
        {
            var progress = new Progress<int>(n => { Progress = $"{n} 장"; Invalidate(); });
            var r = await _session.RunTestAsync(SelectedSet, progress);
            Message = r.Ok ? "" : r.Message;
            Progress = "";
        }
        finally
        {
            Running = false;
            Invalidate();
        }
    }

    /// <summary>틀린 항목의 원인 검사로 편집 화면에서 고친다.</summary>
    public void Fix()
    {
        if (_failedCheckId is not { } id)
            return;
        _edit.SelectedId = id;
        App.Show(_edit);
    }

    private async Task ShowItemAsync(int index)
    {
        try { await ShowItemCoreAsync(index); }
        catch (Exception ex) { FailureDetail = $"오류: {ex.Message}"; Invalidate(); }
    }

    private async Task ShowItemCoreAsync(int index)
    {
        if (_session.TestItemFile(index) is not { } file)
            return;
        string? failed = null;
        var r = await _session.RunImageAsync(file, result =>
        {
            View.Show(result);
            // 처음으로 NG/Error 가 난 단계 = 고칠 검사
            failed = result.Steps.FirstOrDefault(s => s.State is StepState.Ng or StepState.Error)?.Name;
        });
        _failedCheckId = failed is null ? null : _session.Checks.FirstOrDefault(c => c.Name == failed)?.Id;
        FailureDetail = $"{Path.GetFileName(file)}: {r.Message}" + (failed is null ? "" : $"  (원인 검사: {failed})");
        Invalidate();
    }

    private void Invalidate()
    {
        try { App.Invalidate(); }
        catch (InvalidOperationException) { }   // 아직 앱에 붙기 전
    }
}
