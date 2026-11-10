namespace Devinno.Vision;

/// <summary>Pixel format of a <see cref="VisionImage"/>. More formats (16-bit gray) may be added later; code that switches on this value should handle unknown values.</summary>
public enum VisionPixelFormat
{
    /// <summary>8-bit, 3 channels, blue-green-red order (OpenCV <c>CV_8UC3</c>).</summary>
    Bgr8,

    /// <summary>8-bit, 1 channel (OpenCV <c>CV_8UC1</c>; industrial mono cameras deliver this).</summary>
    Gray8,
}
