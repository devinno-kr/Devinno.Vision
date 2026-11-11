using Devinno.UI.App;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;
using SkiaSharp;

namespace Devinno.Vision.Studio.Ui;

/// <summary>목록 한 줄 — 종류 아이콘 · 이름 · 종류 · 상태 배지.</summary>
public sealed record CheckRow(string Id, string Icon, string Name, string Kind, string Badge, string Tone, string ItemClass, string IconClass)
{
    /// <summary>한 줄 글자 (테스트 · 접근성용).</summary>
    public string Text => $"{Name} · {Kind}";
}

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

    public IReadOnlyList<CheckRow> Rows
    {
        get
        {
            var mm = _session.Project?.Calibration is not null;
            return [.. _session.Checks.Select(c =>
            {
                var (badge, tone) = Status(c, mm);
                var on = c.Id == SelectedId;
                return new CheckRow(c.Id, Icon(c), c.Name, Texts.KindLabel(c), badge, tone, on ? "item item-on" : "item", on ? "ico ico-on" : "ico");
            })];
        }
    }

    public string CountText => _session.Checks.Count.ToString();

    private static string Icon(CheckDef c) => c switch
    {
        BlobAlignCheck => "fa-crosshairs",
        BlobCountCheck => "fa-circle-dot",
        PixelCountCheck => "fa-braille",
        BrightnessCheck => "fa-sun",
        LengthCheck => "fa-ruler-horizontal",
        OnnxCheck => "fa-brain",
        _ => "fa-square",
    };

    // 상태 배지 — 할 일이 남았으면 경고색, 아니면 판정 기준을 보여 준다
    private static (string Badge, string Tone) Status(CheckDef c, bool mm)
    {
        if (c is BlobAlignCheck a)
            return a.Reference is null ? (Texts.T("list.badge.teach"), "warning") : (Texts.T("list.badge.taught"), "info");
        if (c.Roi.IsEmpty)
            return (Texts.T("list.badge.noRoi"), "warning");
        if (c is OnnxCheck { Model.Length: 0 })
            return (Texts.T("list.badge.train"), "warning");
        var (badge, tone) = Range(c, mm);
        return c.Role == StepRole.Reason ? (Texts.T("list.badge.reason", badge), "neutral") : (badge, tone);
    }

    private static (string Badge, string Tone) Range(CheckDef c, bool mm)
    {
        return c switch
        {
            BlobCountCheck b => (Texts.T("list.badge.count", b.Min, b.Max), "neutral"),
            PixelCountCheck p => ($"≤{p.Max}", "neutral"),
            BrightnessCheck br => ($"{br.Min:0.#}~{br.Max:0.#}", "neutral"),
            LengthCheck l => ($"{l.Nominal:0.##} {(mm ? "mm" : "px")}", "neutral"),
            OnnxCheck o => ($"≤{o.Threshold:0.##}", "neutral"),
            _ => ("", "neutral"),
        };
    }

    /// <summary>목록 항목을 누르면 그 검사를 고른다.</summary>
    public void Pick(CheckRow row)
    {
        SelectedId = row.Id;
        if (App.CurrentPage is not EditPage)
            App.Show(_edit);   // 속성을 바로 고칠 수 있게
    }

    public bool NoProjectFlag => _session.Project is null;
    public bool HasRows => _session.Checks.Count > 0;
    public bool NoRows => !HasRows;
    public string EmptyText => Texts.T(NoProjectFlag ? "list.empty.noProject" : "list.empty.noChecks");

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
                _status.Ok(Texts.T("list.add.canceled"));
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
            if (await App.MessageBox(Texts.T("list.remove.confirm", check.Name, Texts.KindLabel(check)), Texts.T("list.remove.title"), DvMessageButtons.YesNo) != DvDialogResult.Yes)
            {
                _status.Ok(Texts.T("list.remove.canceled"));
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
