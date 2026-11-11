using System.Globalization;
using System.Text.Json.Nodes;
using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio.Ui;

/// <summary>편집 화면 — 기준 이미지 위 ROI 편집 · 검사별 속성 · 티칭 · 한 장 실행(결과 오버레이). 동작 결과는 상태 줄에.</summary>
public sealed partial class EditPage : DvPage
{
    private readonly StudioSession _session;
    private readonly IFileDialogs _dialogs;
    private readonly StatusBar _status;
    private readonly object _viewGate = new();
    private string? _selectedId;
    private string? _shownBase;
    private long _resultRevision = -1;   // 보여 주는 결과를 만든 로직 판 (-1 = 결과 없음, 기준 이미지만)

    public EditPage(StudioSession session, IFileDialogs dialogs, StatusBar status)
    {
        _session = session;
        _dialogs = dialogs;
        _status = status;
        View.RoiChanged += roi =>
        {
            if (_selectedId is { } id)
                Report(_session.SetRoi(id, roi));
        };
        _session.ProjectChanged += SyncView;
    }

    /// <summary>기준 이미지와 선택 검사의 ROI 를 보여 주는 뷰.</summary>
    public DvVisionView View { get; } = new();

    /// <summary>선택한 검사 id (왼쪽 목록과 같다).</summary>
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            _selectedId = value;
            SyncView();
        }
    }

    private CheckDef? Selected => _selectedId is { } id ? _session.GetCheck(id) : null;

    // ── 상태 ─────────────────────────────────────────────

    public bool HasProject => _session.Project is not null;
    public bool NoProject => !HasProject;
    public bool HasBase => _session.BaseImageFile is not null;
    public bool NoBase => !HasBase;
    public string EmptyTitle => HasProject ? "기준 이미지가 없습니다" : "로직이 없습니다";
    public string EmptyText => HasProject
        ? "[기준 이미지 열기] 로 검사할 부품이 찍힌 이미지를 고르세요.\nROI 는 이 이미지 위에 그립니다."
        : "위 [새 로직] 으로 만들거나 [열기] 로 저장된 로직을 여세요.";
    public bool HasSelection => Selected is not null;
    public bool NoSelection => !HasSelection;
    public bool HasChecks => _session.Checks.Count > 0;
    public string NoSelectionText => HasChecks
        ? "왼쪽 목록에서 검사를 고르면\n여기서 ROI 와 판정 값을 고칩니다."
        : "검사가 없습니다.\n왼쪽 [추가] 로 검사를 더하세요.\n부품 위치는 '블롭 정렬' 을 먼저.";
    public string SelectedTitle => Selected?.Name ?? "검사 속성";

    /// <summary>다음 할 일 — 비활성 버튼의 이유도 이것으로 알려 준다.</summary>
    public string Hint
    {
        get
        {
            if (NoProject) return "다음: 위 [새 로직] 또는 [열기]";
            if (NoBase) return "다음: [기준 이미지] 로 기준 이미지를 고르세요 (티칭 · 실행은 그 뒤에)";
            var checks = _session.Checks;
            if (checks.Count == 0) return "다음: 왼쪽 [추가] 로 검사를 더하세요";
            if (checks[0] is BlobAlignCheck { Reference: null }) return "다음: [기준 티칭] 으로 부품 기준 위치를 저장하세요";
            if (NoSelection) return "다음: 왼쪽 목록에서 검사를 고르세요";
            return ShowingResult ? "결과를 보고 있습니다 — 값을 고치면 결과가 지워집니다" : "이미지에서 끌어 ROI 를 그리고, [실행] 으로 기준 이미지 결과를 확인하세요";
        }
    }

    public bool IsBlobAlign => Selected is BlobAlignCheck;
    public bool IsBlobCount => Selected is BlobCountCheck;
    public bool IsPixelCount => Selected is PixelCountCheck;
    public bool IsBrightness => Selected is BrightnessCheck;
    public bool IsLength => Selected is LengthCheck;
    public bool HasBlob => IsBlobAlign || IsBlobCount;
    public bool HasCode => HasSelection && !IsBlobAlign;

    public string ReferenceText => Selected is BlobAlignCheck { Reference: { } r }
        ? $"기준 위치: x {r.X:0.0}, y {r.Y:0.0}, {r.Angle:0.00}°"
        : "기준 위치 없음 — [기준 티칭] 을 누르세요";

    public string RoiText => Selected is { } c
        ? (c.Roi.IsEmpty ? "ROI: 전체 이미지" : $"ROI: {c.Roi.X:0},{c.Roi.Y:0}  {c.Roi.Width:0}×{c.Roi.Height:0}") + "  (이미지에서 끌어 그리기)"
        : "";

    // ── 공통 속성 ────────────────────────────────────────

    public string CheckName
    {
        get => Selected?.Name ?? "";
        set => Update("Name", value);
    }

    public string Code
    {
        get => Selected switch { BlobCountCheck c => c.Code, PixelCountCheck c => c.Code, BrightnessCheck c => c.Code, LengthCheck c => c.Code, _ => "" };
        set => Update("Code", value);
    }

    // ── 블롭 (정렬 · 개수) ────────────────────────────────

    private BlobParams? Blob => Selected switch { BlobAlignCheck a => a.Blob, BlobCountCheck c => c.Blob, _ => null };

    public ThresholdMode BlobMode
    {
        get => Blob?.Mode ?? ThresholdMode.Otsu;
        set => UpdateBlob("Mode", value.ToString());
    }

    public int BlobThreshold
    {
        get => Blob?.Threshold ?? 128;
        set => UpdateBlob("Threshold", value);
    }

    public bool BlobInvert
    {
        get => Blob?.Invert ?? false;
        set => UpdateBlob("Invert", value);
    }

    public int BlobMinArea
    {
        get => Blob?.MinArea ?? 0;
        set => UpdateBlob("MinArea", value);
    }

    // ── 종류별 ───────────────────────────────────────────

    public int CountMin { get => (Selected as BlobCountCheck)?.Min ?? 0; set => Update("Min", value); }
    public int CountMax { get => (Selected as BlobCountCheck)?.Max ?? 0; set => Update("Max", value); }

    public int PixelLow { get => (Selected as PixelCountCheck)?.Low ?? 0; set => Update("Low", value); }
    public int PixelHigh { get => (Selected as PixelCountCheck)?.High ?? 0; set => Update("High", value); }
    public int PixelMax { get => (Selected as PixelCountCheck)?.Max ?? 0; set => Update("Max", value); }

    public double BrightMin { get => (Selected as BrightnessCheck)?.Min ?? 0; set => Update("Min", value); }
    public double BrightMax { get => (Selected as BrightnessCheck)?.Max ?? 0; set => Update("Max", value); }

    public EdgePolarity LengthPolarity
    {
        get => (Selected as LengthCheck)?.Polarity ?? EdgePolarity.BrightObject;
        set => Update("Polarity", value.ToString());
    }

    public double LengthContrast { get => (Selected as LengthCheck)?.MinContrast ?? 0; set => Update("MinContrast", value); }
    public double LengthNominal { get => (Selected as LengthCheck)?.Nominal ?? 0; set => Update("Nominal", value); }
    public double LengthPlus { get => (Selected as LengthCheck)?.Plus ?? 0; set => Update("Plus", value); }
    public double LengthMinus { get => (Selected as LengthCheck)?.Minus ?? 0; set => Update("Minus", value); }
    public string LengthUnit => _session.Project?.Calibration is null ? "px" : "mm";

    // ── 캘리브레이션 (비우면 없음) ─────────────────────────

    public string Calibration
    {
        get => _session.Project?.Calibration is { } c ? c.MmPerPx.ToString(CultureInfo.InvariantCulture) : "";
        set
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0)
                Report(_session.SetCalibration(null));
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                Report(_session.SetCalibration(v));
            else
                _status.Fail($"숫자가 아닙니다: {text} (예: 0.05, 비우면 캘리브레이션 없음)");
        }
    }

    // ── 마지막 실행 ──────────────────────────────────────

    public IReadOnlyList<ValueLine> LastValues { get; private set; } = [];
    public string LastRun { get; private set; } = "아직 실행하지 않았습니다 — [실행] 또는 [다른 이미지로 실행]";
    public bool HasValues => LastValues.Count > 0;

    /// <summary>결과 띠의 상태 배지 (OK · NG · Error). 결과가 없으면 빈 문자열.</summary>
    public string ResultBadge { get; private set; } = "";
    public string ResultTone { get; private set; } = "neutral";
    public bool HasResultBadge => ResultBadge.Length > 0;

    public string ImageTitle => _session.BaseImageFile is { } f ? $"기준 이미지 · {Path.GetFileName(f)}" : "기준 이미지";
    public string ImageBadge => ShowingResult ? "결과 보는 중" : (HasSelection ? "ROI 편집" : "");
    public string PanelBadge => Selected?.KindLabel ?? "";
    public string CalibrationBadge => _session.Project?.Calibration is null ? "px" : "mm";
    public bool NoValues => !HasValues;
    public bool CannotRun => NoBase || !HasChecks;
    public bool ShowingResult { get { lock (_viewGate) return _resultRevision >= 0; } }

    /// <summary>값 표 한 줄 (표시용).</summary>
    public sealed record ValueLine(string Check, string Name, string Value, string Range, string Judge)
    {
        /// <summary>판정 칸 색 (DvStyle 의 ok · ng).</summary>
        public string JudgeClass => Judge == "OK" ? "ok" : "ng";
    }

    internal SkiaSharp.SKRect OpenBaseBounds => btnOpenBase.Bounds;
    internal SkiaSharp.SKRect TeachBounds => btnTeach.Bounds;
    internal SkiaSharp.SKRect RunBounds => btnRun.Bounds;
    internal SkiaSharp.SKRect RunOtherBounds => btnRunOther.Bounds;


    // ── 좁은 창 ──────────────────────────────────────────
    // 페이지에는 창 크기 API 가 없으므로 본문 줄의 지난 배치 폭(Bounds)으로 판단한다. 바뀌면 한 번 더 그린다 (한 프레임 늦음)
    private bool _compact;

    /// <summary>본문 폭이 좁다 (창을 화면 절반쯤으로 줄였을 때) — 도구 줄을 두 줄로, 옆 칸을 좁게.</summary>
    public bool Compact
    {
        get
        {
            if (body is { } b && b.Bounds.Width > 0)
            {
                var c = b.Bounds.Width < 820;
                if (c != _compact)
                {
                    _compact = c;
                    try { App.Invalidate(); }
                    catch (InvalidOperationException) { }
                }
            }
            return _compact;
        }
    }

    public bool Wide => !Compact;

    /// <summary>오른쪽 속성 칸 폭.</summary>
    public int PanelWidth => Compact ? 260 : 320;

    // ── 명령 ─────────────────────────────────────────────

    public void OpenBase()
    {
        if (_dialogs.PickImage() is { } path)
            Report(_session.SetBaseImage(path));
        else
            _status.Ok("기준 이미지: 취소함");
    }

    public void Teach() => Report(_session.TeachBase());

    /// <summary>기준 이미지로 실행 — 결과 오버레이를 이미지 위에.</summary>
    public async void RunBase()
    {
        try
        {
            if (_session.BaseImageFile is { } file)
                await RunAsync(file);
        }
        catch (Exception ex) { _status.Fail(ex.Message); }
    }

    /// <summary>아무 이미지나 골라 실행 — 결과 오버레이를 이미지 위에.</summary>
    public async void RunOther()
    {
        try
        {
            if (_dialogs.PickImage() is { } file)
                await RunAsync(file);
            else
                _status.Ok("이미지 열어 실행: 취소함");
        }
        catch (Exception ex) { _status.Fail(ex.Message); }
    }

    /// <summary>이미지 한 장을 지금 로직으로 돌려 오버레이 · 값 표를 보여 준다 (테스트가 기다릴 수 있게 Task).</summary>
    internal async Task RunAsync(string file)
    {
        var revision = _session.Revision;
        var r = await _session.RunImageAsync(file, result =>
        {
            lock (_viewGate)
            {
                View.Show(result);
                _resultRevision = revision;
            }
        });
        var name = Path.GetFileName(file);
        if (_session.Revision != revision)
        {
            // 도는 동안 로직이 바뀌었다 — 이 결과는 이미 낡았다
            LastRun = "로직이 바뀌어 앞 결과를 지웠습니다 — 다시 실행하세요";
            LastValues = [];
            ResultBadge = "";
            SyncView();
            return;
        }
        LastRun = $"{name} — {r.Message}";
        (ResultBadge, ResultTone) = r.State switch { "Ok" => ("OK", "success"), "Ng" => ("NG", "danger"), _ => ("Error", "warning") };
        LastValues = [.. r.Values.Select(v => new ValueLine(v.Check, v.Name, $"{v.Value:0.###}{(v.Unit is null ? "" : " " + v.Unit)}",
            v.Nominal is { } n ? $"{n:0.###} ({v.Min:0.###}~{v.Max:0.###})" : $"{v.Min:0.###}~{v.Max:0.###}", v.Ok ? "OK" : "NG"))];
        if (r.Ok) _status.Ok($"실행: {name} → {r.Message}");
        else _status.Fail(r.Message);
    }

    // ── 내부 ─────────────────────────────────────────────

    private void Update(string key, JsonNode? value)
    {
        if (_selectedId is { } id)
            Report(_session.UpdateCheck(id, new JsonObject { [key] = value }));
    }

    private void UpdateBlob(string key, JsonNode? value) => Update("Blob", new JsonObject { [key] = value });

    private void Report(CommandResult r) => _status.Report(r);

    // 기준 이미지가 바뀌었으면 다시 읽고, 선택 검사의 ROI 를 편집 상자로
    private void SyncView()
    {
        lock (_viewGate)
        {
            var file = _session.BaseImageFile;
            var stamp = file is null ? null : $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
            // 결과를 보여 주던 중 로직이 바뀌었으면 결과는 낡았다 → 기준 이미지로 되돌린다
            var stale = _resultRevision >= 0 && _resultRevision != _session.Revision;
            if (stale)
            {
                LastRun = "로직이 바뀌어 앞 결과를 지웠습니다 — 다시 실행하세요";
                LastValues = [];
                ResultBadge = "";
            }
            if (stamp != _shownBase || stale)
            {
                _shownBase = stamp;
                _resultRevision = -1;
                if (file is null)
                {
                    View.Clear();
                }
                else
                {
                    try
                    {
                        using var img = VisionImage.Load(file);
                        View.Show(img);
                    }
                    catch (Exception ex)
                    {
                        View.Clear();
                        _status.Fail($"기준 이미지를 읽을 수 없습니다: {ex.Message}");
                    }
                }
            }
            var roi = Selected?.Roi;
            View.EditRoi = roi is { IsEmpty: false } r ? r : null;
        }
    }
}
