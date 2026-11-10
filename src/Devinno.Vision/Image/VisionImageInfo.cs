namespace Devinno.Vision;

/// <summary>Capture information of an image. Images derived from it (crop, resize, …) share the same information.</summary>
public sealed class VisionImageInfo
{
    /// <summary>When the frame was captured (if possible, when the driver received it).</summary>
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    /// <summary>The camera name, or <see langword="null"/> when not from a camera.</summary>
    public string? Camera { get; init; }

    /// <summary>The camera's frame number, or 0 when unknown.</summary>
    public long FrameNo { get; init; }

    /// <summary>Exposure at capture time in µs, or <see langword="null"/> when unknown.</summary>
    public double? Exposure { get; init; }

    /// <summary>Gain at capture time, or <see langword="null"/> when unknown.</summary>
    public double? Gain { get; init; }

    /// <summary>How many frames the driver dropped right before this one because its queue was full (hardware trigger mode). Frames are never dropped silently.</summary>
    public int Dropped { get; init; }
}
