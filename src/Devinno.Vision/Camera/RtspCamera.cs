using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// A network camera that streams over RTSP (IP cameras, or Studio's camera emulator). A reader thread keeps decoding so the latest frame is always ready.
/// </summary>
/// <remarks>
/// <para>Decoding uses OpenCV's FFmpeg backend (included in OpenCvSharp4.runtime.win). TCP transport is used unless <c>OPENCV_FFMPEG_CAPTURE_OPTIONS</c> is already set.</para>
/// <para>Modes: <see cref="CameraTrigger.Software"/> (<see cref="Grab"/> returns a frame decoded after the call) and <see cref="CameraTrigger.Free"/>
/// (<see cref="WaitFrame"/> returns the next new frame). A stream has no hardware trigger, exposure or gain.</para>
/// <para><b>Delay</b>: a stream shows the scene with a delay (encoding, network, decoding). When another signal says the part is in place,
/// a frame grabbed at once can still show the scene before it; allow for the stream's delay before grabbing.</para>
/// <para><b>Lost stream</b>: when the stream stops, <see cref="Grab"/> and <see cref="WaitFrame"/> throw <see cref="IOException"/> from then on.
/// The camera does not reconnect; dispose it and open a new one.</para>
/// <para>Frames are BGR. The exception messages of this class are Korean.</para>
/// </remarks>
/// <example>
/// <code>
/// await using var camera = await RtspCamera.OpenAsync("rtsp://192.168.0.10:8554/studio");
/// try
/// {
///     using var img = await camera.Grab(ct);      // a frame decoded after this call
///     using var r = await job.RunAsync(img, ct);
/// }
/// catch (IOException ex)
/// {
///     Console.WriteLine(ex.Message);              // the stream stopped: dispose the camera and open it again
/// }
/// </code>
/// </example>
public sealed class RtspCamera : IVisionCamera
{
    private const int ReadTimeoutMs = 5000;

    private readonly VideoCapture _capture;
    private readonly Thread _reader;
    private readonly object _gate = new();
    private Mat? _latest;
    private long _sequence;
    private long _frameNo;
    private Exception? _failure;
    private volatile bool _closing;
    private CameraTrigger _trigger = CameraTrigger.Free;

    private RtspCamera(string url, VideoCapture capture)
    {
        Url = url;
        _capture = capture;
        _reader = new Thread(Read) { IsBackground = true, Name = "RtspCamera " + url };
        _reader.Start();
    }

    /// <summary>The stream address.</summary>
    public string Url { get; }

    /// <summary>The frame size (px), from the stream.</summary>
    public (int Width, int Height) Size => (_capture.FrameWidth, _capture.FrameHeight);

    /// <summary>Opens a stream and waits for its first frame.</summary>
    /// <param name="url">For example <c>rtsp://192.168.0.10:8554/studio</c>.</param>
    /// <param name="timeout">How long to wait for the first frame; default 10 s.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The camera. Dispose it with <c>await using</c>.</returns>
    /// <remarks>Opening is retried until the time limit, so a camera that is still starting up, or a stream that is not published yet, is waited for.</remarks>
    /// <exception cref="ArgumentException"><paramref name="url"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="IOException">The stream cannot be opened or sends no frame in time.</exception>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public static async Task<RtspCamera> OpenAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS")))
            Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp");
        // 스트림이 아직 발행 전이거나 카메라가 켜지는 중일 수 있다 → 제한 시간 안에서 다시 연다
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        VideoCapture capture;
        while (true)
        {
            // CAP_PROP_OPEN_TIMEOUT_MSEC (53) · CAP_PROP_READ_TIMEOUT_MSEC (54): 끊긴 스트림에서 Read 가 오래 막히지 않게 (닫기도 그만큼만 기다린다)
            // CAP_PROP_N_THREADS (70) = 1: 디코더 프레임 스레딩은 스레드 수만큼 프레임을 쌓아 늦게 내보낸다 — 지연 측정 0.8~0.9 s → 0.1~0.2 s
            capture = await Task.Run(() => new VideoCapture(url, VideoCaptureAPIs.FFMPEG, [53, 10_000, 54, ReadTimeoutMs, 70, 1]), ct).ConfigureAwait(false);
            if (capture.IsOpened())
                break;
            capture.Dispose();
            if (watch.Elapsed >= limit)
                throw new IOException($"RTSP 스트림을 열 수 없습니다 ({limit.TotalSeconds:0} 초): {url}");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        var camera = new RtspCamera(url, capture);
        try
        {
            using var first = await camera.NextAsync(0, timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return camera;
        }
        catch
        {
            await camera.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Hardware trigger.</exception>
    public CameraTrigger Trigger
    {
        get => _trigger;
        set => _trigger = value == CameraTrigger.Hardware ? throw new NotSupportedException("RTSP 스트림에는 하드웨어 트리거가 없습니다.") : value;
    }

    /// <inheritdoc/>
    public double ExposureUs { get => double.NaN; set => throw new NotSupportedException("RTSP 스트림의 노출은 바꿀 수 없습니다."); }

    /// <inheritdoc/>
    public double Gain { get => double.NaN; set => throw new NotSupportedException("RTSP 스트림의 게인은 바꿀 수 없습니다."); }

    /// <inheritdoc/>
    /// <exception cref="IOException">The stream stopped.</exception>
    public Task<VisionImage> Grab(CancellationToken ct)
    {
        long after;
        lock (_gate)
            after = _sequence;
        return NextAsync(after, Timeout.InfiniteTimeSpan, ct);
    }

    /// <inheritdoc/>
    /// <exception cref="IOException">The stream stopped.</exception>
    public Task<VisionImage> WaitFrame(CancellationToken ct) => Grab(ct);

    // 순번이 after 보다 큰 프레임을 기다려 사본을 준다
    private async Task<VisionImage> NextAsync(long after, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
            limit.CancelAfter(timeout);
        while (true)
        {
            lock (_gate)
            {
                if (_failure is { } f)
                    throw new IOException($"RTSP 스트림이 끊겼습니다: {Url} ({f.Message})", f);
                if (_sequence > after && _latest is { } m)
                    return VisionImage.From(m, new VisionImageInfo { Time = DateTime.Now, Camera = Url, FrameNo = _frameNo }).Keep();
            }
            try
            {
                await Task.Delay(5, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"RTSP 스트림에서 {timeout.TotalSeconds:0} 초 안에 프레임이 오지 않았습니다: {Url}");
            }
        }
    }

    private void Read()
    {
        try
        {
            while (!_closing)
            {
                var m = new Mat();
                if (!_capture.Read(m) || m.Empty())
                {
                    m.Dispose();
                    if (_closing)
                        return;
                    throw new IOException("프레임을 읽지 못했습니다 (스트림 종료)");
                }
                if (m.Channels() == 1)
                {
                    var bgr = new Mat();
                    Cv2.CvtColor(m, bgr, ColorConversionCodes.GRAY2BGR);
                    m.Dispose();
                    m = bgr;
                }
                Mat? old;
                lock (_gate)
                {
                    old = _latest;
                    _latest = m;
                    _sequence++;
                    _frameNo++;
                }
                old?.Dispose();
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _failure = ex;
        }
    }

    /// <summary>Stops reading and closes the stream.</summary>
    /// <returns>A task that completes when the reader thread has ended (it waits for a read in progress, up to a few seconds on a stalled stream).</returns>
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await Task.Run(() =>
        {
            // 읽는 중에 다른 스레드에서 Release 하면 FFmpeg 디코더가 죽는다 (pthread_frame async_lock 어서션) → 리더가 끝난 뒤에 닫는다
            if (_reader.Join(TimeSpan.FromMilliseconds(ReadTimeoutMs + 2000)))
            {
                _capture.Release();
                _capture.Dispose();
            }
            // 안 끝났으면 (스트림이 막힘) 닫지 않고 둔다 — 프로세스가 죽는 것보다 낫다. 리더는 읽기 제한 시간 뒤에 스스로 끝난다
        }).ConfigureAwait(false);
        lock (_gate)
        {
            _latest?.Dispose();
            _latest = null;
        }
    }
}
