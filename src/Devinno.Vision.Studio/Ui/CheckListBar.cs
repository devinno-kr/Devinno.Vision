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
                return new CheckRow(c.Id, Icon(c), c.Name, c.KindLabel, badge, tone, on ? "item item-on" : "item", on ? "ico ico-on" : "ico");
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
        _ => "fa-square",
    };

    // 상태 배지 — 할 일이 남았으면 경고색, 아니면 판정 기준을 보여 준다
    private static (string Badge, string Tone) Status(CheckDef c, bool mm)
    {
        if (c is BlobAlignCheck a)
            return a.Reference is null ? ("티칭 필요", "warning") : ("티칭됨", "info");
        if (c.Roi.IsEmpty)
            return ("ROI 없음", "warning");
        return c switch
        {
            BlobCountCheck b => ($"{b.Min}~{b.Max}개", "neutral"),
            PixelCountCheck p => ($"≤{p.Max}", "neutral"),
            BrightnessCheck br => ($"{br.Min:0.#}~{br.Max:0.#}", "neutral"),
            LengthCheck l => ($"{l.Nominal:0.##} {(mm ? "mm" : "px")}", "neutral"),
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
