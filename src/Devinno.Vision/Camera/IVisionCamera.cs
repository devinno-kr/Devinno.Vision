namespace Devinno.Vision;

/// <summary>When the camera takes a picture.</summary>
public enum CameraTrigger
{
    /// <summary>The app asks (<see cref="IVisionCamera.Grab"/>): one fresh frame per call.</summary>
    Software,

    /// <summary>A PLC or sensor triggers the camera directly; every frame is queued in order (<see cref="IVisionCamera.WaitFrame"/>). Overflow drops the oldest and reports it in <see cref="VisionImageInfo.Dropped"/>.</summary>
    Hardware,

    /// <summary>Free running (live view): only the latest frame is kept (<see cref="IVisionCamera.WaitFrame"/>).</summary>
    Free,
}

/// <summary>
/// A camera driver. Each driver is its own package (for example <c>Devinno.Vision.FlashCap</c>). Frames are returned as images owned by the caller (<c>using</c>);
/// the driver hands over pixels it owns (never a buffer the SDK will reuse).
/// </summary>
/// <remarks>
/// <para>Drivers: <see cref="FileCamera"/> (image files of a folder), <see cref="RtspCamera"/> (network streams), and <c>FlashCapCamera</c> (USB cameras, package Devinno.Vision.FlashCap).
/// Write app code against this interface so that saved images can replace the camera in tests.</para>
/// <para>When to take a picture is the app's decision (a PLC signal, a button, a timer). Dispose the camera with <c>await using</c>.</para>
/// </remarks>
/// <example>
/// <code>
/// await using IVisionCamera camera = await RtspCamera.OpenAsync("rtsp://192.168.0.10:8554/studio");
///
/// using var img = await camera.Grab(ct);          // one fresh frame
/// using var r = await job.RunAsync(img, ct);
/// </code>
/// </example>
public interface IVisionCamera : IAsyncDisposable
{
    /// <summary>The trigger mode. Setting a mode the device cannot do throws <see cref="NotSupportedException"/>.</summary>
    CameraTrigger Trigger { get; set; }

    /// <summary>Exposure in µs. <see cref="double.NaN"/> when unknown; setting throws <see cref="NotSupportedException"/> on devices that cannot change it (most built-in webcams).</summary>
    double ExposureUs { get; set; }

    /// <summary>Gain. <see cref="double.NaN"/> when unknown; setting throws <see cref="NotSupportedException"/> when the device cannot change it.</summary>
    double Gain { get; set; }

    /// <summary>Software trigger: takes one fresh frame (taken after this call).</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The frame, owned by the caller (dispose it).</returns>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    Task<VisionImage> Grab(CancellationToken ct);

    /// <summary>Hardware or free mode: waits for the next frame in the queue.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The frame, owned by the caller (dispose it).</returns>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    Task<VisionImage> WaitFrame(CancellationToken ct);
}
