using Devinno.UI.Widgets;
using OpenCvSharp;
using SkiaSharp;

namespace Devinno.Vision.UI;

// <VisionView source="{Vm.FrontView}"/> — DvVisionView 를 그리고, EditRoi 를 끌어 고친다
internal sealed class VisionViewWidget : DvWidget
{
    private const float Handle = 14;   // 모서리 손잡이를 잡는 거리 (화면 px)

    private long _painted = -1;
    private Affine _sourceToScreen = Affine.Identity;
    private bool _hasImage;

    // 끌기 상태
    private const double MinSize = 4;   // 새로 그리거나 줄일 때 이보다 작으면 무시 (이미지 px)
    private enum Drag { None, Move, Corner, New }
    private Drag _drag;
    private int _corner;
    private Point2d _start;
    private VisionRoi _origin;
    private VisionRoi? _preview;

    public override SKSize Measure(DvWidgetContext ctx, float? availableWidth) => new(640, 480);

    public override int? FrameInterval(DvWidgetContext ctx)
        => ctx.Value("source") is DvVisionView v && v.Version != _painted ? 16 : null;

    public override void Paint(SKCanvas canvas, SKRect bounds, DvWidgetContext ctx)
    {
        if (ctx.Value("source") is not DvVisionView view)
        {
            ctx.DrawText(canvas, "VisionView: bind source to a DvVisionView", bounds.Left + 8, bounds.Top + 20, 13, ctx.Color("text-muted"));
            return;
        }
        _painted = view.Version;
        view.Use((snap, editRoi, warnDisabled, disabledFormat) =>
        {
            using var bg = new SKPaint { Color = ctx.Color("surface-sunken", SKColors.Black) };
            canvas.DrawRect(bounds, bg);
            _hasImage = snap?.Image is not null;
            if (snap?.Image is not { } img)
                return;

            // 비율 유지 맞춤 (가운데)
            var scale = Math.Min(bounds.Width / img.Width, bounds.Height / img.Height);
            var dx = bounds.Left + (bounds.Width - img.Width * scale) / 2;
            var dy = bounds.Top + (bounds.Height - img.Height * scale) / 2;
            var dest = new SKRect(dx, dy, dx + img.Width * scale, dy + img.Height * scale);
            canvas.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Linear));
            _sourceToScreen = snap.SourceToDisplay.Then(new Affine(scale, 0, dx, 0, scale, dy));

            canvas.Save();
            canvas.ClipRect(dest);
            foreach (var o in snap.Overlays)
                DrawOverlay(canvas, ctx, o, snap);
            if (snap.StepName is null && (_preview ?? editRoi) is { } roi)
                DrawEditRoi(canvas, ctx, roi);
            canvas.Restore();

            // 판정 · 경고
            var y = bounds.Top + 6;
            if (snap.State is { } state)
            {
                var (text, tone) = state switch
                {
                    VisionState.Ok => ("OK", "tone-success"),
                    VisionState.Ng => (snap.Codes.Count > 0 ? "NG  " + string.Join(", ", snap.Codes) : "NG", "tone-danger"),
                    _ => ("ERROR", "tone-warning"),
                };
                using var badge = new SKPaint { IsAntialias = true, Color = ctx.Color(tone) };
                var w = ctx.MeasureText(text, 18, weight: 700) + 16;
                canvas.DrawRoundRect(new SKRect(bounds.Left + 6, y, bounds.Left + 6 + w, y + 28), 4, 4, badge);
                ctx.DrawText(canvas, text, bounds.Left + 14, y + 21, 18, SKColors.White, weight: 700);
                if (snap.StepName is { } sn)
                    ctx.DrawText(canvas, $"[{sn}]", bounds.Left + w + 14, y + 20, 14, SKColors.White);
                y += 34;
            }
            if (warnDisabled && snap.Disabled.Count > 0)
            {
                var text = string.Format(disabledFormat, snap.Disabled.Count, string.Join(", ", snap.Disabled));
                using var band = new SKPaint { Color = ctx.Color("tone-warning").WithAlpha(220) };
                canvas.DrawRect(new SKRect(bounds.Left, y, bounds.Right, y + 24), band);
                ctx.DrawText(canvas, text, bounds.Left + 8, y + 17, 14, SKColors.Black, weight: 700);
            }
            if (snap.Error is { } err)
            {
                using var band = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
                canvas.DrawRect(new SKRect(bounds.Left, bounds.Bottom - 24, bounds.Right, bounds.Bottom), band);
                ctx.DrawText(canvas, err, bounds.Left + 8, bounds.Bottom - 7, 13, SKColors.White);
            }
        });
    }

    private void DrawOverlay(SKCanvas canvas, DvWidgetContext ctx, VisionOverlay o, ViewSnapshot snap)
    {
        var pts = o.Points.Select(p => ToSk(_sourceToScreen.Map(p))).ToArray();
        if (pts.Length == 0)
            return;
        var tone = o.Kind == VisionOverlayKind.Roi
            ? (snap.StepStates.GetValueOrDefault(o.Step) switch
            {
                StepState.Ok => "tone-success",
                StepState.Ng => "tone-danger",
                StepState.Error => "tone-warning",
                _ => "text-muted",
            })
            : "tone-info";
        using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = o.Kind == VisionOverlayKind.Roi ? 2 : 1.5f, Color = ctx.Color(tone, SKColors.Cyan) };
        switch (o.Kind)
        {
            case VisionOverlayKind.Roi or VisionOverlayKind.Box or VisionOverlayKind.Contour:
                using (var path = new SKPath())
                {
                    path.AddPoly(pts, close: true);
                    canvas.DrawPath(path, pen);
                }
                break;
            case VisionOverlayKind.Line:
                if (pts.Length >= 2)
                    canvas.DrawLine(pts[0], pts[1], pen);
                break;
            case VisionOverlayKind.Point:
                canvas.DrawLine(pts[0].X - 4, pts[0].Y, pts[0].X + 4, pts[0].Y, pen);
                canvas.DrawLine(pts[0].X, pts[0].Y - 4, pts[0].X, pts[0].Y + 4, pen);
                break;
        }
        if (o.Label is { } label)
        {
            var at = o.Kind == VisionOverlayKind.Line && pts.Length >= 2 ? new SKPoint((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2 - 4) : new SKPoint(pts[0].X + 2, pts[0].Y - 4);
            // 회색 이미지 위에서도 읽히게 어두운 받침
            var bold = o.Kind == VisionOverlayKind.Roi;
            const float size = 15;   // 12 → 15: 실제 창에서 길이 라벨이 안 읽혀 키움
            var w = ctx.MeasureText(label, size, bold ? 700 : 400);
            using var back = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
            canvas.DrawRect(new SKRect(at.X - 3, at.Y - size, at.X + w + 3, at.Y + 4), back);
            ctx.DrawText(canvas, label, at.X, at.Y, size, pen.Color, weight: bold ? 700 : 400);
        }
    }

    private void DrawEditRoi(SKCanvas canvas, DvWidgetContext ctx, VisionRoi roi)
    {
        if (roi.IsEmpty)
            return;
        var pts = roi.Corners().Select(p => ToSk(_sourceToScreen.Map(p))).ToArray();
        using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = ctx.Color("primary", SKColors.Yellow), PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
        using var path = new SKPath();
        path.AddPoly(pts, close: true);
        canvas.DrawPath(path, pen);
        if (roi.Shape != VisionRoiShape.Rect)
            return;
        using var fill = new SKPaint { IsAntialias = true, Color = pen.Color };
        foreach (var p in pts)
            canvas.DrawRect(new SKRect(p.X - 5, p.Y - 5, p.X + 5, p.Y + 5), fill);
    }

    // ── ROI 편집 ──────────────────────────────────────

    public override bool OnPointerDown(DvWidgetContext ctx, SKPoint point)
    {
        if (ctx.Value("source") is not DvVisionView view || !_hasImage || view.EditRoi is not { } roi)
            return false;
        _start = ToImage(point);
        _origin = roi;
        if (!roi.IsEmpty && roi.Shape == VisionRoiShape.Rect)
        {
            var corners = roi.Corners();
            for (var i = 0; i < 4; i++)
            {
                var s = _sourceToScreen.Map(corners[i]);
                if (Math.Abs(s.X - point.X) <= Handle && Math.Abs(s.Y - point.Y) <= Handle)
                {
                    _drag = Drag.Corner;
                    _corner = i;
                    return true;
                }
            }
        }
        // ROI 가 있으면 옮기기(안쪽) · 크기(모서리)만. 바깥을 눌러 새로 그리면 실수 한 번에 설정이 날아간다 (2026-11-11 사용자)
        if (!roi.IsEmpty)
        {
            if (!Inside(roi, _start))
                return false;
            _drag = Drag.Move;
            return true;
        }
        _drag = Drag.New;
        return true;
    }

    public override void OnPointerMove(DvWidgetContext ctx, SKPoint point) => _preview = Apply(ToImage(point));

    public override void OnPointerUp(DvWidgetContext ctx, SKPoint point)
    {
        var drag = _drag;
        var result = Apply(ToImage(point));
        _preview = null;
        _drag = Drag.None;
        if (result is not { IsEmpty: false } r || ctx.Value("source") is not DvVisionView view)
            return;
        if (r == _origin)
            return;   // 제자리에서 누르고 뗌 — 바꾼 것이 없다
        if (drag is Drag.New or Drag.Corner && (r.Width < MinSize || r.Height < MinSize))
            return;   // 실수로 눌린 아주 작은 사각형
        view.CommitRoi(r);
    }

    public override void OnPointerCancel(DvWidgetContext ctx)
    {
        _preview = null;
        _drag = Drag.None;
    }

    private VisionRoi? Apply(Point2d p)
    {
        double ddx = p.X - _start.X, ddy = p.Y - _start.Y;
        switch (_drag)
        {
            case Drag.Move:
                return _origin with { X = Math.Round(_origin.X + ddx), Y = Math.Round(_origin.Y + ddy) };
            case Drag.Corner:
                {
                    // 잡은 모서리의 맞은편이 고정
                    var c = _origin.Corners();
                    var fixedPt = c[(_corner + 2) % 4];
                    return Rect(fixedPt, p);
                }
            case Drag.New:
                return Rect(_start, p);
            default:
                return null;
        }
    }

    private static VisionRoi Rect(Point2d a, Point2d b)
    {
        double x0 = Math.Round(Math.Min(a.X, b.X)), y0 = Math.Round(Math.Min(a.Y, b.Y));
        double x1 = Math.Round(Math.Max(a.X, b.X)), y1 = Math.Round(Math.Max(a.Y, b.Y));
        return VisionRoi.Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    private static bool Inside(VisionRoi roi, Point2d p)
    {
        // ROI 좌표계로 돌려서 본다 (회전 사각형 포함)
        var rad = -roi.PlacedAngle * Math.PI / 180;
        var c = roi.Center;
        double vx = p.X - c.X, vy = p.Y - c.Y;
        double u = vx * Math.Cos(rad) - vy * Math.Sin(rad), v = vx * Math.Sin(rad) + vy * Math.Cos(rad);
        return Math.Abs(u) <= roi.Width / 2 && Math.Abs(v) <= roi.Height / 2;
    }

    private Point2d ToImage(SKPoint p) => _sourceToScreen.Invert().Map(new Point2d(p.X, p.Y));

    private static SKPoint ToSk(Point2d p) => new((float)p.X, (float)p.Y);
}
