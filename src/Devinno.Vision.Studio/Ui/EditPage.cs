using System.Globalization;
using System.Text.Json.Nodes;
using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio.Ui;

/// <summary>편집 화면 — 기준 이미지 위 ROI 편집 · 검사별 속성 · 티칭 · 한 장 실행.</summary>
public sealed partial class EditPage : DvPage
{
    private readonly StudioSession _session;
    private readonly IFileDialogs _dialogs;
    private readonly object _viewGate = new();
    private string? _selectedId;
    private string? _shownBase;

    public EditPage(StudioSession session, IFileDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;
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
    public string EmptyText => HasProject ? "기준 이미지를 여세요" : "새 로직을 만들거나 여세요";
    public string Message { get; private set; } = "";
    public bool HasMessage => Message.Length > 0;
    public bool HasSelection => Selected is not null;
    public bool NoSelection => !HasSelection;
    public string SelectedTitle => Selected is { } c ? $"{c.Name} · {c.KindLabel}" : "검사를 고르세요";

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
                Message = $"숫자가 아닙니다: {text}";
        }
    }

    // ── 마지막 실행 ──────────────────────────────────────

    public IReadOnlyList<ValueLine> LastValues { get; private set; } = [];
    public string LastRun { get; private set; } = "";

    /// <summary>값 표 한 줄 (표시용).</summary>
    public sealed record ValueLine(string Check, string Name, string Value, string Range, string Judge);

    // ── 명령 ─────────────────────────────────────────────

    public void OpenBase()
    {
        if (_dialogs.PickImage() is { } path)
            Report(_session.SetBaseImage(path));
    }

    public void Teach() => Report(_session.TeachBase());

    public async void RunBase()
    {
        try { await RunBaseAsync(); }
        catch (Exception ex) { Message = $"오류: {ex.Message}"; }
    }

    private async Task RunBaseAsync()
    {
        if (_session.BaseImageFile is not { } file)
        {
            Message = "기준 이미지를 먼저 지정하세요";
            return;
        }
        var r = await _session.RunImageAsync(file);
        LastRun = r.Ok ? $"결과: {r.Message}" : r.Message;
        LastValues = [.. r.Values.Select(v => new ValueLine(v.Check, v.Name, $"{v.Value:0.###}{(v.Unit is null ? "" : " " + v.Unit)}",
            v.Nominal is { } n ? $"{n:0.###} ({v.Min:0.###}~{v.Max:0.###})" : $"{v.Min:0.###}~{v.Max:0.###}", v.Ok ? "OK" : "NG"))];
        Message = r.Ok ? "" : r.Message;
    }

    // ── 내부 ─────────────────────────────────────────────

    private void Update(string key, JsonNode? value)
    {
        if (_selectedId is { } id)
            Report(_session.UpdateCheck(id, new JsonObject { [key] = value }));
    }

    private void UpdateBlob(string key, JsonNode? value) => Update("Blob", new JsonObject { [key] = value });

    private void Report(CommandResult r) => Message = r.Ok ? "" : r.Message;

    // 기준 이미지가 바뀌었으면 다시 읽고, 선택 검사의 ROI 를 편집 상자로
    private void SyncView()
    {
        lock (_viewGate)
        {
            var file = _session.BaseImageFile;
            var stamp = file is null ? null : $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
            if (stamp != _shownBase)
            {
                _shownBase = stamp;
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
                        Message = $"기준 이미지를 읽을 수 없습니다: {ex.Message}";
                    }
                }
            }
            var roi = Selected?.Roi;
            View.EditRoi = roi is { IsEmpty: false } r ? r : null;
        }
    }
}
