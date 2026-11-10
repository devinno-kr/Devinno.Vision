using OpenCvSharp;
using SkiaSharp;

namespace Devinno.Vision;

// 화면용 사본: 축소 BGRA + 원본(실행 입력) 좌표 → 표시 이미지 좌표
internal sealed class ViewSnapshot : IDisposable
{
    public SKImage? Image { get; private init; }
    public Affine SourceToDisplay { get; private init; } = Affine.Identity;
    public double Scale { get; private init; } = 1;
    public VisionState? State { get; set; }
    public string? Error { get; set; }
    public string? Job { get; set; }
    public string? StepName { get; set; }
    public List<VisionOverlay> Overlays { get; set; } = [];
    public Dictionary<string, StepState> StepStates { get; set; } = [];
    public List<string> Disabled { get; set; } = [];
    public List<string> Codes { get; set; } = [];

    public static ViewSnapshot FromImage(Mat mat, int maxSize, Affine? sourceToImage)
    {
        var scale = Math.Min(1.0, (double)Math.Max(1, maxSize) / Math.Max(mat.Width, mat.Height));
        using var small = new Mat();
        if (scale < 1)
            Cv2.Resize(mat, small, new OpenCvSharp.Size(Math.Max(1, (int)(mat.Width * scale)), Math.Max(1, (int)(mat.Height * scale))), 0, 0, InterpolationFlags.Area);
        else
            mat.CopyTo(small);
        using var bgra = new Mat();
        Cv2.CvtColor(small, bgra, small.Channels() == 1 ? ColorConversionCodes.GRAY2BGRA : ColorConversionCodes.BGR2BGRA);
        var info = new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var img = SKImage.FromPixelCopy(info, bgra.Data, (int)bgra.Step());
        double sx = (double)bgra.Width / mat.Width, sy = (double)bgra.Height / mat.Height;
        var toDisplay = new Affine(sx, 0, 0, 0, sy, 0);
        return new ViewSnapshot
        {
            Image = img,
            Scale = sx,
            SourceToDisplay = (sourceToImage ?? Affine.Identity).Then(toDisplay),
        };
    }

    public void Dispose() => Image?.Dispose();
}
