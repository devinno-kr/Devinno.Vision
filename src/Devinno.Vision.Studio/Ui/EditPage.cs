using System.Globalization;
using System.Text.Json.Nodes;
using Devinno.UI.App;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio.Ui;

/// <summary>기준 설정의 두 방식.</summary>
public enum RefTab
{
    /// <summary>기준 이미지 (표준 검사).</summary>
    Image,

    /// <summary>양품 모델.</summary>
    Model,
}

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
            if (IsModelTab)
                Report(_session.SetGoodRoi(roi));
            else if (_selectedId is { } id)
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

    /// <summary>화면에 이미지가 있다 (기준 이미지, 없으면 양품 폴더의 첫 장).</summary>
    public bool HasImage => ShownFile is not null;
    public bool NoImage => !HasImage;
    private string? ShownFile => _session.BaseImageFile ?? _session.GoodPreviewFile;
    public string EmptyTitle => HasProject ? Texts.T("edit.empty.noBaseTitle") : Texts.T("edit.empty.noProjectTitle");
    public bool HasGoodModel => _session.Project?.GoodModel is not null;
    public string EmptyText => HasProject
        ? Texts.T("edit.empty.noBaseText")
        : Texts.T("edit.empty.noProjectText");
    public bool HasSelection => Selected is not null;
    public bool NoSelection => !HasSelection;
    public bool HasChecks => _session.Checks.Count > 0;
    public string NoSelectionText => HasChecks
        ? Texts.T("edit.noSelection.pick")
        : Texts.T("edit.noSelection.noChecks");
    public string SelectedTitle => Selected?.Name ?? Texts.T("edit.checkProps");

    /// <summary>다음 할 일 — 비활성 버튼의 이유도 이것으로 알려 준다.</summary>
    public string Hint
    {
        get
        {
            if (NoProject) return Texts.T("edit.hint.noProject");
            if (IsModelTab) return ModelHint;
            if (NoBase) return Texts.T("edit.hint.noBase");
            var checks = _session.Checks;
            if (checks.Count == 0) return Texts.T("edit.hint.noChecks");
            if (checks[0] is BlobAlignCheck { Reference: null }) return Texts.T("edit.hint.teach");
            if (NoSelection) return Texts.T("edit.hint.pick");
            return ShowingResult ? Texts.T("edit.hint.result") : Texts.T("edit.hint.draw");
        }
    }

    public bool IsBlobAlign => Selected is BlobAlignCheck;
    public bool IsBlobCount => Selected is BlobCountCheck;
    public bool IsPixelCount => Selected is PixelCountCheck;
    public bool IsBrightness => Selected is BrightnessCheck;
    public bool IsLength => Selected is LengthCheck;
    public bool IsOnnx => Selected is OnnxCheck;
    public bool HasBlob => IsBlobAlign || IsBlobCount;

    // ── ONNX 검사 (불량 모델) ─────────────────────────────

    public double OnnxThreshold { get => (Selected as OnnxCheck)?.Threshold ?? 0.5; set => Update("Threshold", value); }

    public string DefectFolderText => _selectedId is { } id && _session.DefectFolder(id) is { } f
        ? Texts.T("edit.defectFolder.path", f.Length <= 40 ? f : "…" + f[^39..])
        : Texts.T("edit.defectFolder.none");

    public string DefectStatusText
    {
        get
        {
            if (_session.TrainingProgress is { } t && IsOnnx)
                return t.Stage == "crop" ? Texts.T("edit.training.crop", t.Done, t.Total) : Texts.T("edit.training.epoch", t.Done, t.Total);
            if (Selected is not OnnxCheck o)
                return "";
            if (o.Model.Length == 0)
                return _session.HasPython ? Texts.T("edit.defect.noModel") : Texts.T("edit.defect.noModelNoPython");
            var labels = _session.DefectLabels(o.Id);
            var s = _session.DefectSummary(o.Id);
            return Texts.T("edit.defect.model", string.Join(" · ", labels)) + (s?.Accuracy is { } a ? Texts.T("edit.defect.accuracy", a * 100) : "");
        }
    }

    public bool CannotTrainDefect => IsTraining || _selectedId is not { } id || _session.DefectFolder(id) is null || !_session.HasPython;
    public bool NoPython => IsOnnx && !_session.HasPython;
    public bool SettingUpPython { get; private set; }

    public void PickDefectFolder()
    {
        if (_selectedId is not { } id)
            return;
        if (_dialogs.PickFolder() is { } folder)
            Report(_session.SetDefectFolder(id, folder));
        else
            _status.Ok(Texts.T("edit.defectFolder.canceled"));
    }

    public void ImportOnnx()
    {
        if (_selectedId is not { } id)
            return;
        if (_dialogs.PickModel() is { } file)
            Report(_session.ImportOnnx(id, file));
        else
            _status.Ok(Texts.T("edit.importOnnx.canceled"));
    }

    public async void TrainDefect()
    {
        try
        {
            if (_selectedId is { } id)
            {
                _status.Ok(Texts.T("edit.defect.trainStart"));
                Report(await _session.TrainDefectModelAsync(id));
            }
        }
        catch (Exception ex) { _status.Fail(ex.Message); }
    }

    public async void SetupPython()
    {
        if (SettingUpPython)
            return;
        SettingUpPython = true;
        try
        {
            var r = await _session.SetupPythonAsync(new Progress<string>(line => _status.Ok(Texts.T("edit.python.progress", line))));
            Report(r);
        }
        catch (Exception ex) { _status.Fail(ex.Message); }
        finally
        {
            SettingUpPython = false;
            try { App.Invalidate(); } catch (InvalidOperationException) { }
        }
    }
    public bool HasCode => HasSelection && !IsBlobAlign && !IsOnnx;   // ONNX 검사의 NG 코드는 불량 이름

    /// <summary>판정 / 사유 (정렬 검사는 늘 판정).</summary>
    public bool CanHaveRole => HasSelection && !IsBlobAlign;

    public StepRole Role
    {
        get => Selected?.Role ?? StepRole.Judge;
        set => Update("Role", value.ToString());
    }

    public string RoleText => Role == StepRole.Reason
        ? Texts.T("edit.role.reasonText")
        : Texts.T("edit.role.judgeText");

    public string ReferenceText => Selected is BlobAlignCheck { Reference: { } r }
        ? Texts.T("edit.reference.pos", r.X, r.Y, r.Angle)
        : Texts.T("edit.reference.none");

    public string RoiText => Selected is { } c
        ? (c.Roi.IsEmpty ? Texts.T("edit.roi.whole") : Texts.T("edit.roi.rect", c.Roi.X, c.Roi.Y, c.Roi.Width, c.Roi.Height)) + Texts.T("edit.roi.drawHint")
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
                _status.Fail(Texts.T("edit.calibration.notNumber", text));
        }
    }

    // ── 마지막 실행 ──────────────────────────────────────

    public IReadOnlyList<ValueLine> LastValues { get; private set; } = [];
    // 고정 문구는 키로 들고 있다가 읽을 때 지금 언어로 바꾼다 (언어를 바꾸면 따라가게). 실행 결과 줄은 글 그대로
    private string? _lastRunKey = "edit.lastRun.none";
    private string _lastRunText = "";

    public string LastRun
    {
        get => _lastRunKey is { } key ? Texts.T(key) : _lastRunText;
        private set => (_lastRunKey, _lastRunText) = (null, value);
    }
    public bool HasValues => LastValues.Count > 0;

    /// <summary>결과 띠의 상태 배지 (OK · NG · Error). 결과가 없으면 빈 문자열.</summary>
    public string ResultBadge { get; private set; } = "";
    public string ResultTone { get; private set; } = "neutral";
    public bool HasResultBadge => ResultBadge.Length > 0;

    public string ImageTitle => _session.BaseImageFile is { } f ? Texts.T("edit.imageTitle.base", Path.GetFileName(f))
        : ShownFile is { } g ? Texts.T("edit.imageTitle.good", Path.GetFileName(g)) : Texts.T("edit.baseImage");
    public string ImageBadge => ShowingResult ? Texts.T("edit.badge.result") : IsModelTab ? Texts.T("edit.badge.region") : (HasSelection ? Texts.T("edit.badge.roi") : "");
    public string PanelBadge => Selected is { } c ? Texts.KindLabel(c) : "";
    public string CalibrationBadge => _session.Project?.Calibration is null ? "px" : "mm";
    public bool NoValues => !HasValues;
    public bool CannotRun => NoImage || (!HasChecks && !HasGoodModel) || _session.IsTraining;
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
    internal SkiaSharp.SKRect TrainBounds => btnTrain.Bounds;
    internal SkiaSharp.SKRect ViewBounds => view.Bounds;


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

    // ── 기준: 기준 이미지 / 양품 모델 ────────────────────

    private RefTab _tab;

    public RefTab Tab
    {
        get => _tab;
        set
        {
            _tab = value;
            SyncView();
        }
    }

    public bool IsImageTab => _tab == RefTab.Image;
    public bool IsModelTab => _tab == RefTab.Model;
    public bool ImageWide => IsImageTab && Wide;
    public bool ImageCompact => IsImageTab && Compact;
    public bool IsTraining => _session.IsTraining;
    public bool CannotPickGood => NoProject || IsTraining;
    public bool CannotTrain => NoProject || IsTraining || _session.GoodImages().Count == 0;
    public bool CannotRemoveGood => !HasGoodModel || IsTraining;
    public bool CannotResetGoodRoi => NoProject || IsTraining || _session.GoodRoi.IsEmpty;

    public string GoodFolderText
    {
        get
        {
            if (_session.GoodFolder is not { } f)
                return Texts.T("edit.goodFolder.none");
            var n = _session.GoodImages().Count;
            return Texts.T("edit.goodFolder.count", f.Length <= 48 ? f : "…" + f[^47..], n) + (n < StudioSession.RecommendedGoods ? Texts.T("edit.goodFolder.recommended", StudioSession.RecommendedGoods) : "");
        }
    }

    public string GoodRoiText => _session.GoodRoi is { IsEmpty: false } r ? Texts.T("edit.goodRoi.rect", r.X, r.Y, r.Width, r.Height) : Texts.T("edit.goodRoi.whole");

    /// <summary>양품 모델 상태 한 줄.</summary>
    public string GoodStatusText
    {
        get
        {
            if (_session.TrainingProgress is { } t)
                return Texts.T(t.Stage switch { "crop" => "edit.training.crop", "learn" => "edit.training.learn", _ => "edit.training.limit" }, t.Done, t.Total);
            if (IsTraining)
                return Texts.T("edit.good.preparing");
            if (_session.Project?.GoodModel is not { } g)
                return Texts.T("edit.good.none");
            if (!_session.Project.GoodModelIsCurrent)
                return Texts.T("edit.good.stale");
            return _session.GoodSummary is { } s
                ? Texts.T("edit.good.learned", s.Learned, s.Calibration, g.Max, s.MsPerImage) + (s.Skipped.Count > 0 ? Texts.T("edit.good.skipped", s.Skipped.Count) : "")
                : Texts.T("edit.good.learnedLimit", g.Max);
        }
    }

    /// <summary>카드 위 양품 모델 배지.</summary>
    public string GoodBadge => IsTraining ? Texts.T("edit.badge.training") : _session.Project?.GoodModel is null ? "" : _session.Project.GoodModelIsCurrent ? Texts.T("edit.goodModel") : Texts.T("edit.badge.retrain");
    public string GoodTone => IsTraining ? "info" : _session.Project?.GoodModelIsCurrent == false ? "warning" : "success";
    public bool HasGoodBadge => GoodBadge.Length > 0;

    private string ModelHint
    {
        get
        {
            if (IsTraining) return Texts.T("edit.modelHint.training");
            if (_session.GoodFolder is null) return Texts.T("edit.modelHint.folder");
            if (!HasGoodModel) return _session.GoodRoi.IsEmpty
                ? Texts.T("edit.modelHint.region")
                : Texts.T("edit.modelHint.train");
            if (_session.Project?.GoodModelIsCurrent == false) return Texts.T("edit.modelHint.retrain");
            return Texts.T("edit.modelHint.done");
        }
    }

    public void PickGoodFolder()
    {
        if (_dialogs.PickFolder() is { } folder)
            Report(_session.SetGoodFolder(folder));
        else
            _status.Ok(Texts.T("edit.goodFolder.canceled"));
    }

    public void ResetGoodRoi() => Report(_session.SetGoodRoi(default));

    public void RemoveGood() => Report(_session.RemoveGoodModel());

    public void CancelTrain() => Report(_session.CancelTraining());

    public async void TrainGood()
    {
        try
        {
            await TrainAsync();
        }
        catch (Exception ex) { _status.Fail(ex.Message); }
    }

    /// <summary>양품 학습 (테스트가 기다릴 수 있게 Task).</summary>
    internal async Task TrainAsync()
    {
        _status.Ok(Texts.T("edit.good.trainStart"));
        Report(await _session.TrainGoodModelAsync());
    }

    /// <summary>오른쪽 속성 칸 폭.</summary>
    public int PanelWidth => Compact ? 260 : 320;

    // ── 명령 ─────────────────────────────────────────────

    public void OpenBase()
    {
        if (_dialogs.PickImage() is { } path)
            Report(_session.SetBaseImage(path));
        else
            _status.Ok(Texts.T("edit.baseImage.canceled"));
    }

    public void Teach() => Report(_session.TeachBase());

    /// <summary>기준 이미지로 실행 — 결과 오버레이를 이미지 위에.</summary>
    public async void RunBase()
    {
        try
        {
            if (ShownFile is { } file)
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
                _status.Ok(Texts.T("edit.runOther.canceled"));
        }
        catch (Exception ex) { _status.Fail(ex.Message); }
    }

    /// <summary>이미지 한 장을 지금 로직으로 돌려 오버레이 · 값 표를 보여 준다 (테스트가 기다릴 수 있게 Task).</summary>
    internal async Task RunAsync(string file)
    {
        var revision = _session.Revision;
        if (_session.IsTraining)
        {
            _status.Fail(StudioSession.Training);
            return;
        }
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
            _lastRunKey = "edit.lastRun.stale";
            LastValues = [];
            ResultBadge = "";
            SyncView();
            return;
        }
        LastRun = r.Ms > 0 ? $"{name} — {r.Message}  ·  {r.Ms:0} ms" : $"{name} — {r.Message}";
        (ResultBadge, ResultTone) = r.State switch { "Ok" => ("OK", "success"), "Ng" => ("NG", "danger"), _ => ("Error", "warning") };
        LastValues = [.. r.Values.Select(v => new ValueLine(v.Check, v.Name, $"{v.Value:0.###}{(v.Unit is null ? "" : " " + v.Unit)}",
            v.Nominal is { } n ? $"{n:0.###} ({v.Min:0.###}~{v.Max:0.###})" : $"{v.Min:0.###}~{v.Max:0.###}", v.Ok ? "OK" : "NG"))];
        if (r.Ok) _status.Ok(Texts.T("edit.ran", name, r.Message));
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
            var file = ShownFile;
            var stamp = file is null ? null : $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
            // 결과를 보여 주던 중 로직이 바뀌었으면 결과는 낡았다 → 기준 이미지로 되돌린다
            var stale = _resultRevision >= 0 && _resultRevision != _session.Revision;
            if (stale)
            {
                _lastRunKey = "edit.lastRun.stale";
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
                        _status.Fail(Texts.T("edit.baseImage.unreadable", ex.Message));
                    }
                }
            }
            var roi = IsModelTab ? _session.GoodRoi : Selected?.Roi;
            // 빈 ROI 도 그대로 넘긴다 — 뷰는 빈 ROI 면 새로 그리기를 받는다 (null 은 "편집 안 함")
            View.EditRoi = roi;
        }
    }
}
