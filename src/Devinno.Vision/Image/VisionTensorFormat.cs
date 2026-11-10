namespace Devinno.Vision;

/// <summary>
/// How <see cref="VisionImage.ToTensor"/> turns pixels into a model input: channel count and order, scaling and normalization.
/// The layout is always channels-first (CHW), the common ONNX image layout.
/// </summary>
/// <param name="Mean">Mean per channel subtracted after scaling (its length = channel count: 1 or 3).</param>
/// <param name="Std">Standard deviation per channel divided after subtracting the mean (same length as <paramref name="Mean"/>).</param>
/// <param name="Rgb">Channel order for 3 channels: <see langword="true"/> = R, G, B (most models trained in Python); <see langword="false"/> = B, G, R.</param>
/// <param name="Scale">Multiplies the 0–255 value first (1/255 = 0…1).</param>
/// <remarks>Wrong channel order or normalization gives no error — only worse results. Match exactly what the model was trained with.</remarks>
public sealed record VisionTensorFormat(float[] Mean, float[] Std, bool Rgb = true, float Scale = 1f / 255)
{
    /// <summary>ImageNet normalization, RGB, 0…1 then mean (0.485, 0.456, 0.406) / std (0.229, 0.224, 0.225) — models from torchvision and most pretrained CNNs.</summary>
    public static VisionTensorFormat ImageNet { get; } = new([0.485f, 0.456f, 0.406f], [0.229f, 0.224f, 0.225f]);

    /// <summary>RGB scaled to 0…1, no normalization.</summary>
    public static VisionTensorFormat Unit { get; } = new([0f, 0f, 0f], [1f, 1f, 1f]);

    /// <summary>One gray channel scaled to 0…1.</summary>
    public static VisionTensorFormat Gray { get; } = new([0f], [1f]);

    /// <summary>Channel count (1 or 3).</summary>
    public int Channels => Mean.Length;
}
