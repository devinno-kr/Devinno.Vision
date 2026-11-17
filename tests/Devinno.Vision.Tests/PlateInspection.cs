using Devinno.Vision;

// Plate 샘플 검사 — 1단계 완료 기준. 검사 항목: 위쪽 구멍(좌 · 우) · 노치 · 길이(아래쪽 구멍 피치) (+ 정렬)
// 샘플은 합성 이미지다 (tools/gen_plate.py): 프레스 플레이트 100×64 mm, 4 px/mm, 구멍 4개 · 가운데 보어 · 위쪽 노치
public sealed class PlateSetup
{
    public BlobParams Body { get; set; } = new() { MinArea = 20000 };
    public VisionPose? BodyRef { get; set; }

    public VisionRoi HoleL { get; set; } = VisionRoi.Rect(138, 130, 60, 60);
    public VisionRoi HoleR { get; set; } = VisionRoi.Rect(442, 130, 60, 60);
    // 구멍은 아주 어둡다. Otsu 는 구멍이 없을 때 표면 질감을 둘로 나눠 가짜 구멍을 만든다 → 고정 문턱
    public BlobParams Hole { get; set; } = new() { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 };

    public VisionRoi Notch { get; set; } = VisionRoi.Rect(238, 116, 36, 20);
    // 노치가 뚫려 있으면 이 영역은 배경(어둡다). 안 뚫리면 판(밝다)이 보인다
    public int NotchBright { get; set; } = 100;
    public int NotchMaxBright { get; set; } = 200;

    public VisionRoi Width { get; set; } = VisionRoi.Rect(135, 312, 370, 16);
    // 아래쪽 두 구멍의 바깥 가장자리 사이 (피치 76 mm + 지름 8 mm = 84 mm = 336 px). 불량은 구멍이 2.7 mm(11 px) 이상 어긋나 있다
    public double WidthMin { get; set; } = 332;
    public double WidthMax { get; set; } = 340;
}

public static class PlateInspection
{
    public static string SampleDir => Path.Combine(AppContext.BaseDirectory, "Samples", "Plate");

    public static string Sample(string name) => Path.Combine(SampleDir, name);

    public static PlateSetup DefaultSetup() => new();

    public static VisionJob<PlateSetup> CreateJob() => new VisionJob<PlateSetup>("플레이트")
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
        .Add("노치", roi: s => s.Notch, (ctx, s) =>
        {
            ctx.Value("노치 밝은 점 수", Brightness.Count(ctx.Image, s.NotchBright, 255), max: s.NotchMaxBright, code: "NOTCH");
            ctx.Value("노치 편차", Brightness.StdDev(ctx.Image));
        })
        .Add("길이", roi: s => s.Width, (ctx, s) =>
        {
            var e = Edge.Caliper(ctx.Image, EdgePolarity.DarkObject, minContrast: 8);   // 밝은 판 위의 어두운 구멍 둘
            ctx.Draw(e);
            ctx.Value("길이", e.Distance, min: s.WidthMin, max: s.WidthMax, unit: "px", code: "LENGTH");
        });
}
