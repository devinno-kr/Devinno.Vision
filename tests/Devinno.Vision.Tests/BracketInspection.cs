using Devinno.Vision;

// Bracket 샘플 검사 — 1단계 완료 기준. 검사 항목: 구멍(좌 · 우) · 상단 변색 · 길이 (+ 정렬)
public sealed class BracketSetup
{
    public BlobParams Body { get; set; } = new() { MinArea = 20000 };
    public VisionPose? BodyRef { get; set; }

    public VisionRoi HoleL { get; set; } = VisionRoi.Rect(194, 251, 60, 60);
    public VisionRoi HoleR { get; set; } = VisionRoi.Rect(417, 251, 60, 60);
    // 구멍은 아주 어둡다 (≈30). Otsu 는 구멍이 없을 때 표면 질감을 둘로 나눠 가짜 구멍을 만든다 → 고정 문턱
    public BlobParams Hole { get; set; } = new() { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 };

    public VisionRoi Tab { get; set; } = VisionRoi.Rect(268, 192, 134, 36);
    public int TabDark { get; set; } = 100;
    public int TabMaxDark { get; set; } = 200;

    public VisionRoi Width { get; set; } = VisionRoi.Rect(100, 305, 470, 30);
    // 측정: 양품 409.8~411.2 px (18번 회전 포함), 불량 384.9~399.9 px → 경계 405
    public double WidthMin { get; set; } = 405;
    public double WidthMax { get; set; } = 417;
}

public static class BracketInspection
{
    public static string SampleDir => Path.Combine(AppContext.BaseDirectory, "Samples", "Bracket");

    public static string Sample(string name) => Path.Combine(SampleDir, name);

    public static BracketSetup DefaultSetup() => new();

    public static VisionJob<BracketSetup> CreateJob() => new VisionJob<BracketSetup>("브래킷")
        .Add("정렬", pose: "본체", (ctx, s) =>
        {
            var part = Blob.Find(ctx.Image, s.Body).FirstOrDefault();
            if (part is null) { ctx.Fail("부품 없음"); return; }
            ctx.SetPose(VisionPose.FromBlob(part, s.BodyRef), s.BodyRef);
        })
        .Add("왼쪽 구멍", roi: s => s.HoleL, (ctx, s) =>
        {
            var holes = Blob.Find(ctx.Image, s.Hole);
            ctx.Draw(holes);
            ctx.Value("왼쪽 구멍 수", holes.Length, min: 1, max: 1, code: "HOLE");
        })
        .Add("오른쪽 구멍", roi: s => s.HoleR, (ctx, s) =>
        {
            var holes = Blob.Find(ctx.Image, s.Hole);
            ctx.Draw(holes);
            ctx.Value("오른쪽 구멍 수", holes.Length, min: 1, max: 1, code: "HOLE");
        })
        .Add("상단 변색", roi: s => s.Tab, (ctx, s) =>
        {
            ctx.Value("변색 점 수", Brightness.Count(ctx.Image, 0, s.TabDark), max: s.TabMaxDark, code: "DISCOLOR");
            ctx.Value("변색 편차", Brightness.StdDev(ctx.Image));
        })
        .Add("길이", roi: s => s.Width, (ctx, s) =>
        {
            var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject, minContrast: 8);   // 오른쪽 모서리는 그림자로 완만하다
            ctx.Draw(e);
            ctx.Value("길이", e.Distance, min: s.WidthMin, max: s.WidthMax, unit: "px", code: "LENGTH");
        });
}
