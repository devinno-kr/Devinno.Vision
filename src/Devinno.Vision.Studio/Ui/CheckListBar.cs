using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using SkiaSharp;

namespace Devinno.Vision.Studio.Ui;

/// <summary>목록 한 줄.</summary>
public sealed record CheckRow(string Id, string Text);

/// <summary>왼쪽 검사 목록 — 추가 · 삭제(확인 창) · 순서. 선택은 편집 화면의 선택과 같다. 결과는 상태 줄에.</summary>
public sealed partial class CheckListBar : DvBar
{
    private readonly StudioSession _session;
    private readonly EditPage _edit;
    private readonly StatusBar _status;

    public CheckListBar(StudioSession session, EditPage edit, StatusBar status)
    {
        _session = session;
        _edit = edit;
        _status = status;
    }

    public IReadOnlyList<CheckRow> Rows => [.. _session.Checks.Select(c => new CheckRow(c.Id, $"{(c is BlobAlignCheck ? "◎" : "●")}  {c.Name}  ·  {c.KindLabel}"))];

    public bool NoProjectFlag => _session.Project is null;
    public bool HasRows => _session.Checks.Count > 0;
    public bool NoRows => !HasRows;
    public string EmptyText => NoProjectFlag ? "로직이 없습니다.\n위 [새 로직] 또는 [열기] 로 시작하세요." : "검사가 없습니다.\n[추가] 로 검사를 더하세요.\n부품 위치는 '블롭 정렬' 을 먼저.";

    public bool NoSelection => SelectedId is null || _session.GetCheck(SelectedId) is null;
    public bool CannotUp => NoSelection || Index(SelectedId!) <= 0;
    public bool CannotDown => NoSelection || Index(SelectedId!) >= _session.Checks.Count - 1;

    public string? SelectedId
    {
        get => _edit.SelectedId;
        set => _edit.SelectedId = value;
    }

    internal SKRect AddBounds => btnAdd.Bounds;
    internal SKRect RemoveBounds => btnRemove.Bounds;

    public async void Add()
    {
        try
        {
            if (await App.ShowDialog<AddCheckDialog, AddCheckRequest>() is not { } req)
            {
                _status.Ok("검사 추가: 취소함");
                return;
            }
            var r = _session.AddCheck(req.Kind, req.Name);
            _status.Report(r);
            if (r.Ok)
            {
                SelectedId = r.Id;
                App.Show(_edit);   // 새 검사의 속성을 바로 고칠 수 있게
            }
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    /// <summary>짧게 누르면 확인 창 → [예] 일 때만 삭제.</summary>
    public async void Remove()
    {
        try
        {
            if (SelectedId is not { } id || _session.GetCheck(id) is not { } check)
                return;
            if (await App.MessageBox($"정말 삭제할까요?\n\n{check.Name} ({check.KindLabel})", "검사 삭제", DvMessageButtons.YesNo) != DvDialogResult.Yes)
            {
                _status.Ok("삭제 취소함");
                return;
            }
            var r = _session.RemoveCheck(id);
            _status.Report(r);
            if (r.Ok)
                SelectedId = null;
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    public void Up() => Move(-1);

    public void Down() => Move(+1);

    private int Index(string id) => _session.Checks.ToList().FindIndex(c => c.Id == id);

    private void Move(int delta)
    {
        if (SelectedId is not { } id)
            return;
        var index = Index(id);
        if (index < 0 || index + delta < 0 || index + delta >= _session.Checks.Count)
            return;
        _status.Report(_session.MoveCheck(id, index + delta));
    }
}
