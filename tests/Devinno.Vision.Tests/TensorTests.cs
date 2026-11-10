using Devinno.Vision;
using OpenCvSharp;

public class TensorTests
{
    [Fact]
    public void Bgr_to_rgb_chw_with_imagenet_normalization()
    {
        // 파랑 255, 초록 0, 빨강 128 인 4×2 이미지
        using var m = new Mat(2, 4, MatType.CV_8UC3, new Scalar(255, 0, 128));
        using var img = VisionImage.From(m);
        var t = img.ToTensor(4, 2);
        Assert.Equal(3 * 2 * 4, t.Length);
        Assert.Equal((128 / 255f - 0.485f) / 0.229f, t[0], 4);        // R 평면 첫 값
        Assert.Equal((0 - 0.456f) / 0.224f, t[8], 4);                  // G 평면
        Assert.Equal((1f - 0.406f) / 0.225f, t[16], 4);                // B 평면
    }

    [Fact]
    public void Resizes_and_converts_gray()
    {
        using var m = new Mat(10, 20, MatType.CV_8UC1, Scalar.All(51));
        using var img = VisionImage.From(m);
        var g = img.ToTensor(5, 5, VisionTensorFormat.Gray);
        Assert.Equal(25, g.Length);
        Assert.All(g, v => Assert.Equal(0.2f, v, 4));
        var rgb = img.ToTensor(5, 5, VisionTensorFormat.Unit);   // 흑백 → 3 채널 복제
        Assert.Equal(75, rgb.Length);
        Assert.Equal(0.2f, rgb[50], 4);
    }

    [Fact]
    public void Bgr_order_is_kept_when_asked()
    {
        using var m = new Mat(1, 1, MatType.CV_8UC3, new Scalar(255, 0, 0));
        using var img = VisionImage.From(m);
        var t = img.ToTensor(1, 1, VisionTensorFormat.Unit with { Rgb = false });
        Assert.Equal([1f, 0f, 0f], t);
    }
}
