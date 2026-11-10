using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using SkiaSharp;

namespace Devinno.Vision.Studio.Ui;

/// <summary>목록 한 줄.</summary>
public sealed record CheckRow(string Id, string Text);

/// <summary>왼쪽 검사 목록 — 추가 · 삭제 · 순서. 선택은 편집 화면의 선택과 같다.</summary>
public sealed partial class CheckListBar : DvBar
{
    private readonly StudioSession _session;
    private readonly EditPage _edit;

    public CheckListBar(StudioSession session, EditPage edit)
    {
        _session = session;
        _edit = edit;
    }

    public IReadOnlyList<CheckRow> Rows => [.. _session.Checks.Select(c => new CheckRow(c.Id, $"{(c is BlobAlignCheck ? "◎" : "●")}  {c.Name}  ·  {c.KindLabel}"))];

    public bool NoProjectFlag => _session.Project is null;

    public bool NoSelection => SelectedId is null || _session.GetCheck(SelectedId) is null;

    public string? SelectedId
    {
        get => _edit.SelectedId;
        set => _edit.SelectedId = value;
    }

    public string Message { get; private set; } = "";

    public bool HasMessage => Message.Length > 0;

    internal SKRect AddBounds => btnAdd.Bounds;

    public async void Add()
    {
        try { await AddAsync(); }
        catch (Exception ex) { Message = $"오류: {ex.Message}"; }
    }

    private async Task AddAsync()
    {
        if (await App.ShowDialog<AddCheckDialog, AddCheckRequest>() is not { } req)
            return;
        var r = _session.AddCheck(req.Kind, req.Name);
        Message = r.Ok ? "" : r.Message;
        if (r.Ok)
            SelectedId = r.Id;
    }

    public void Remove()
    {
        if (SelectedId is not { } id)
            return;
        var r = _session.RemoveCheck(id);
        Message = r.Ok ? "" : r.Message;
        if (r.Ok)
            SelectedId = null;
    }

    public void Up() => Move(-1);

    public void Down() => Move(+1);

    private void Move(int delta)
    {
        if (SelectedId is not { } id)
            return;
        var list = _session.Checks;
        var index = list.ToList().FindIndex(c => c.Id == id);
        if (index < 0 || index + delta < 0 || index + delta >= list.Count)
            return;
        var r = _session.MoveCheck(id, index + delta);
        Message = r.Ok ? "" : r.Message;
    }
}
