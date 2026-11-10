using FlashCap;
using OpenCvSharp;

namespace Devinno.Vision.FlashCap;

/// <summary>
/// UVC camera (USB webcams, laptop cameras) through FlashCap. Software and free trigger modes; exposure and gain are not controlled yet.
/// </summary>
/// <remarks>
/// The camera streams continuously. <see cref="Grab"/> returns the first frame that arrives after the call (never an old buffered one);
/// <see cref="WaitFrame"/> (free mode) returns the latest frame not returned yet. Frames are decoded only when asked for.
/// </remarks>
public sealed class FlashCapCamera : IVisionCamera
{
    private readonly object _gate = new();
    private CaptureDevice _device = null!;
    private readonly bool _gray;
    private byte[]? _latest;
    private long _latestNo;
    private DateTimeOffset _latestTime;
    private long _returnedNo;
    private TaskCompletionSource _arrived = NewSignal();
    private CameraTrigger _trigger = CameraTrigger.Software;

    private FlashCapCamera(string name, bool gray)
    {
        Name = name;
        _gray = gray;
    }

    /// <summary>The device name.</summary>
    public string Name { get; }

    /// <summary>Names of the connected video devices.</summary>
    /// <returns>Device names (the same device may appear once per Windows backend).</returns>
    public static IReadOnlyList<string> Devices()
        => [.. new CaptureDevices().EnumerateDescriptors().Where(d => d.Characteristics.Length > 0).Select(d => d.Name).Distinct()];

    /// <summary>Opens a camera and starts streaming.</summary>
    /// <param name="device">Part of the device name; <see langword="null"/> = the first device.</param>
    /// <param name="width">Wanted width; 0 = the largest.</param>
    /// <param name="height">Wanted height; 0 = any for that width.</param>
    /// <param name="gray">Deliver <see cref="VisionPixelFormat.Gray8"/> instead of BGR.</param>
    /// <param name="ct">Cancels opening.</param>
    /// <returns>The camera; dispose it with <c>await using</c>.</returns>
    /// <exception cref="InvalidOperationException">No matching device or resolution.</exception>
    public static async Task<FlashCapCamera> OpenAsync(string? device = null, int width = 0, int height = 0, bool gray = false, CancellationToken ct = default)
    {
        var descriptor = new CaptureDevices().EnumerateDescriptors()
            .Where(d => d.Characteristics.Length > 0)
            .FirstOrDefault(d => device is null || d.Name.Contains(device, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(device is null ? "No camera found." : $"No camera whose name contains '{device}'. Found: {string.Join(", ", Devices())}");

        // 원하는 크기 중 fps 가 높은 것, 같으면 압축 안 된 형식(YUYV — 디코딩 비용 없음)
        var chosen = descriptor.Characteristics
            .Where(c => c.PixelFormat != PixelFormats.Unknown)
            .Where(c => width == 0 || c.Width == width)
            .Where(c => height == 0 || c.Height == height)
            .OrderByDescending(c => width == 0 ? c.Width * c.Height : 0)
            .ThenByDescending(c => (double)c.FramesPerSecond.Numerator / c.FramesPerSecond.Denominator)
            .ThenBy(c => c.IsCompression)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{descriptor.Name} has no mode {width}×{height}. Modes: {string.Join(", ", descriptor.Characteristics.Select(c => c.ToString()))}");

        var cam = new FlashCapCamera(descriptor.Name, gray);
        cam._device = await descriptor.OpenAsync(chosen, cam.OnFrame, ct).ConfigureAwait(false);
        await cam._device.StartAsync(ct).ConfigureAwait(false);
        cam.Mode = chosen.ToString();
        return cam;
    }

    /// <summary>The streaming mode (size, format, fps).</summary>
    public string Mode { get; private set; } = "";

    /// <summary><see cref="CameraTrigger.Software"/> or <see cref="CameraTrigger.Free"/>; <see cref="CameraTrigger.Hardware"/> throws <see cref="NotSupportedException"/> (UVC has no trigger input).</summary>
    public CameraTrigger Trigger
    {
        get => _trigger;
        set => _trigger = value == CameraTrigger.Hardware ? throw new NotSupportedException("UVC cameras have no hardware trigger.") : value;
    }

    /// <summary>Not controlled yet: <see cref="double.NaN"/>; setting throws <see cref="NotSupportedException"/>.</summary>
    public double ExposureUs { get => double.NaN; set => throw new NotSupportedException("Exposure control is not supported by this driver yet."); }

    /// <summary>Not controlled yet: <see cref="double.NaN"/>; setting throws <see cref="NotSupportedException"/>.</summary>
    public double Gain { get => double.NaN; set => throw new NotSupportedException("Gain control is not supported by this driver yet."); }

    /// <summary>Returns the first frame that arrives after this call.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The frame, owned by the caller.</returns>
    public async Task<VisionImage> Grab(CancellationToken ct)
    {
        long after;
        lock (_gate)
            after = _latestNo;
        return await NextAfter(after, ct).ConfigureAwait(false);
    }

    /// <summary>Free mode: the latest frame not returned yet (waits if there is none).</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The frame, owned by the caller.</returns>
    public Task<VisionImage> WaitFrame(CancellationToken ct)
    {
        long after;
        lock (_gate)
            after = _returnedNo;
        return NextAfter(after, ct);
    }

    /// <summary>Stops streaming and closes the device.</summary>
    /// <returns>A task.</returns>
    public async ValueTask DisposeAsync()
    {
        try { await _device.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* 이미 멈춘 장치 */ }
        await _device.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<VisionImage> NextAfter(long after, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_latest is { } bytes && _latestNo > after)
                {
                    _returnedNo = _latestNo;
                    return Decode(bytes, _latestNo, _latestTime);
                }
                wait = _arrived.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private VisionImage Decode(byte[] bytes, long no, DateTimeOffset time)
    {
        // FlashCap 은 JPEG 은 JPEG 그대로, YUYV 등은 BMP 로 변환해 준다 — 둘 다 ImDecode 로 읽힌다
        using var mat = Cv2.ImDecode(bytes, _gray ? ImreadModes.Grayscale : ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidOperationException($"{Name}: could not decode frame {no}.");
        return VisionImage.From(mat, new VisionImageInfo { Camera = Name, FrameNo = no, Time = time });
    }

    // FlashCap 스레드: 복사만 하고 바로 돌려준다 (디코딩은 요청할 때)
    private void OnFrame(PixelBufferScope scope)
    {
        var bytes = scope.Buffer.CopyImage();
        var no = scope.Buffer.FrameIndex;
        TaskCompletionSource signal;
        lock (_gate)
        {
            _latest = bytes;
            _latestNo = Math.Max(_latestNo + 1, no);
            _latestTime = DateTimeOffset.Now;
            signal = _arrived;
            _arrived = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
