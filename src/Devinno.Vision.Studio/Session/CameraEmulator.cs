using System.Diagnostics;
using System.Net.Sockets;
using Devinno.Communication.Modbus.TCP;
using OpenCvSharp;

namespace Devinno.Vision.Studio.Session;

/// <summary>카메라 에뮬레이터 설정.</summary>
/// <param name="ModbusPort">Modbus TCP 포트 (Studio 가 슬레이브).</param>
/// <param name="RtspPort">RTSP 포트.</param>
/// <param name="Path">RTSP 경로 (rtsp://PC:포트/경로).</param>
/// <param name="Fps">초당 프레임.</param>
/// <param name="SlideMs">다음 이미지로 밀려 들어오는 시간 (0 = 바로 바꿈).</param>
/// <param name="SettleMs">새 이미지를 내보내기 시작한 뒤 D0 을 0 으로 내리기까지 (RTSP 지연 여유).</param>
/// <param name="LocalOnly">이 PC 에서만 (127.0.0.1) 연다 — Windows 방화벽 허용 창이 뜨지 않지만 다른 PC 는 붙을 수 없다.</param>
public sealed record EmulatorOptions(int ModbusPort = 502, int RtspPort = 8554, string Path = "studio", int Fps = 10, int SlideMs = 300, int SettleMs = 500, bool LocalOnly = false);

/// <summary>에뮬레이터 상태 (화면 · MCP).</summary>
public sealed record EmulatorStatus(bool Running, string Url, int ModbusPort, int Index, int Count, string File, bool Busy, int Clients, string? Error);

/// <summary>
/// Studio 가 가상 카메라가 된다: 테스트 폴더의 이미지를 RTSP 로 내보내고, Modbus TCP 슬레이브로 한 장씩 넘긴다.
/// <list type="bullet">
/// <item>D100 (홀딩 레지스터 100) ← 1: 받는 즉시 D100 = 0, D0 = 1 (넘기는 중) — 쓰기 응답 전에 바뀐다.</item>
/// <item>다음 이미지가 밀려 들어오고 <see cref="EmulatorOptions.SettleMs"/> 뒤 D0 = 0 → 그때 프레임을 잡으면 새 이미지다.</item>
/// <item>넘기는 중에 또 1 이 오면 하나만 기억했다가 이어서 넘긴다. 마지막 다음은 처음.</item>
/// <item>HMI 는 D0 = 0 (정지) 일 때 프레임을 잡아 검사한다. 에뮬레이터가 멈춰 있으면 D100 = 1 은 0 으로만 돌린다.</item>
/// </list>
/// </summary>
internal sealed class CameraEmulator : IDisposable
{
    public const int BusyRegister = 0;
    public const int NextRegister = 100;

    private readonly IReadOnlyList<string> _files;
    private readonly EmulatorOptions _options;
    private readonly IFrameSink _sink;
    private readonly object _gate = new();
    private ModbusTCPSlave? _slave;
    private Thread? _loop;
    private volatile bool _stop;
    private Mat? _current;
    private int _index;
    private int _pending;
    private string? _error;
    private (int W, int H) _size;

    public CameraEmulator(IReadOnlyList<string> files, EmulatorOptions options, IFrameSink sink)
    {
        if (files.Count == 0)
            throw new ArgumentException(Texts.T("emu.noFiles"), nameof(files));
        _files = files;
        _options = options;
        _sink = sink;
    }

    public string Url => $"rtsp://{(_options.LocalOnly ? "127.0.0.1" : Environment.MachineName)}:{_options.RtspPort}/{_options.Path}";

    /// <summary>장 · 넘기는 중 · 오류가 바뀌었다 (에뮬레이터 스레드에서 온다).</summary>
    public event Action? Changed;

    public EmulatorStatus Status
    {
        get
        {
            lock (_gate)
            {
                var busy = _slave is { } s && Read(s, BusyRegister) == 1;
                return new EmulatorStatus(_loop is not null && !_stop, Url, _options.ModbusPort, _index, _files.Count, Path.GetFileName(_files[_index]), busy, _slave?.ClientCount ?? 0, _error);
            }
        }
    }

    /// <summary>첫 이미지로 송출과 Modbus 슬레이브를 시작한다. 실패하면 예외 (포트 사용 중, ffmpeg 없음 등) — 시작한 것은 정리된다.</summary>
    public void Start()
    {
        _current = LoadFrame(_files[0], null);
        _size = (_current.Width, _current.Height);
        try
        {
            _sink.Start(_size.W, _size.H, _options.Fps);
            var slave = new ModbusTCPSlave { LocalIP = _options.LocalOnly ? "127.0.0.1" : "0.0.0.0", LocalPort = _options.ModbusPort, Slave = 1, HoldingRegisters = new int[200] };
            slave.WordWritten += OnWritten;
            slave.Start();
            _slave = slave;
            _loop = new Thread(Loop) { IsBackground = true, Name = "CameraEmulator" };
            _loop.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // 쓰기 응답 전에 불린다 (ModbusTCPSlave.HandleFrame) → 마스터가 응답을 받을 때 D0 은 이미 1
    private void OnWritten(object? sender, ModbusTCPSlave.WrittenEventArgs e)
    {
        if (sender is not ModbusTCPSlave s || NextRegister < e.StartAddress || NextRegister >= e.StartAddress + e.Length)
            return;
        lock (s.SyncRoot)
        {
            var hr = s.HoldingRegisters!;
            if (hr[NextRegister] != 1)
                return;
            hr[NextRegister] = 0;
            if (_stop)
                return;
            hr[BusyRegister] = 1;
        }
        Interlocked.Increment(ref _pending);
    }

    private void Loop()
    {
        var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, _options.Fps));
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        Mat? from = null;
        Mat? to = null;
        var slideFrames = Math.Max(0, (int)Math.Round(_options.SlideMs / 1000.0 * _options.Fps));
        var slideStep = 0;
        var settleUntil = TimeSpan.MinValue;
        try
        {
            while (!_stop)
            {
                // 다음 장 요청
                if (to is null && settleUntil == TimeSpan.MinValue && Volatile.Read(ref _pending) > 0)
                {
                    Interlocked.Decrement(ref _pending);
                    int index;
                    lock (_gate)
                        index = (_index + 1) % _files.Count;
                    from = _current!.Clone();
                    to = LoadFrame(_files[index], _size);
                    lock (_gate)
                        _index = index;
                    slideStep = 0;
                    Changed?.Invoke();
                }

                Mat frame;
                if (to is not null && slideStep < slideFrames)
                {
                    frame = Slide(from!, to, (slideStep + 1.0) / (slideFrames + 1));
                    slideStep++;
                }
                else
                {
                    if (to is not null)
                    {
                        // 밀려 들어오기 끝 — 새 이미지를 내보내기 시작한다
                        Mat? old;
                        lock (_gate)
                        {
                            old = _current;
                            _current = to;
                        }
                        old?.Dispose();
                        from?.Dispose();
                        (from, to) = (null, null);
                        settleUntil = clock.Elapsed + TimeSpan.FromMilliseconds(_options.SettleMs);
                    }
                    lock (_gate)
                        frame = _current!.Clone();
                }
                using (frame)
                    _sink.Write(frame);

                if (settleUntil != TimeSpan.MinValue && clock.Elapsed >= settleUntil)
                {
                    settleUntil = TimeSpan.MinValue;
                    // 기억해 둔 요청이 없을 때만 D0 = 0 (있으면 바로 이어서 넘긴다)
                    if (Volatile.Read(ref _pending) == 0)
                    {
                        lock (_slave!.SyncRoot)
                            _slave.HoldingRegisters![BusyRegister] = 0;
                        Changed?.Invoke();
                    }
                }

                next += period;
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                else
                    next = clock.Elapsed;   // 늦었으면 따라잡지 않는다
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _error = ex.Message;
            _stop = true;
            if (_slave is { } s)
                lock (s.SyncRoot)
                    s.HoldingRegisters![BusyRegister] = 0;
            Changed?.Invoke();
        }
        finally
        {
            from?.Dispose();
            to?.Dispose();
        }
    }

    // 새 이미지가 오른쪽에서 밀려 들어온다 (t = 0 → 1)
    private static Mat Slide(Mat from, Mat to, double t)
    {
        int w = from.Width, h = from.Height;
        var shift = Math.Clamp((int)Math.Round(w * t), 0, w);
        var frame = new Mat(h, w, MatType.CV_8UC3);
        if (shift < w)
            using (var src = new Mat(from, new Rect(shift, 0, w - shift, h)))
            using (var dst = new Mat(frame, new Rect(0, 0, w - shift, h)))
                src.CopyTo(dst);
        if (shift > 0)
            using (var src = new Mat(to, new Rect(0, 0, shift, h)))
            using (var dst = new Mat(frame, new Rect(w - shift, 0, shift, h)))
                src.CopyTo(dst);
        return frame;
    }

    // BGR 3채널, 짝수 크기 (H.264), 첫 장과 같은 크기로
    private static Mat LoadFrame(string file, (int W, int H)? size)
    {
        // imread 는 경로를 ANSI 코드 페이지로 넘긴다 (다른 언어의 폴더 이름에서 실패) → 파일은 .NET 으로 읽는다
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException(Texts.T("emu.cannotReadImage", file), ex); }
        using var raw = bytes.Length == 0 ? new Mat() : Cv2.ImDecode(bytes, ImreadModes.Color);
        if (raw.Empty())
            throw new IOException(Texts.T("emu.cannotReadImage", file));
        var (w, h) = size ?? (raw.Width & ~1, raw.Height & ~1);
        var m = new Mat();
        if (raw.Width == w && raw.Height == h)
            raw.CopyTo(m);
        else if (size is null)
            using (var crop = new Mat(raw, new Rect(0, 0, w, h)))
                crop.CopyTo(m);
        else
            Cv2.Resize(raw, m, new OpenCvSharp.Size(w, h));
        return m;
    }

    private static int Read(ModbusTCPSlave s, int address)
    {
        lock (s.SyncRoot)
            return s.HoldingRegisters![address];
    }

    public void Dispose()
    {
        _stop = true;
        _loop?.Join(TimeSpan.FromSeconds(3));
        if (_slave is { } s)
        {
            s.WordWritten -= OnWritten;
            s.Dispose();
        }
        _sink.Dispose();
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }
}

/// <summary>에뮬레이터가 프레임을 내보내는 곳 (RTSP, 테스트에서는 메모리).</summary>
internal interface IFrameSink : IDisposable
{
    void Start(int width, int height, int fps);

    void Write(Mat bgr);
}

/// <summary>MediaMTX(RTSP 서버) + ffmpeg(H.264 인코딩 · 발행). 둘 다 자식 프로세스로 띄우고 끝낼 때 정리한다.</summary>
internal sealed class RtspSink(EmulatorOptions options) : IFrameSink
{
    private Process? _server;
    private Process? _ffmpeg;
    private string? _config;

    /// <summary>MediaMTX 실행 파일: 환경 변수 DEVINNO_STUDIO_MEDIAMTX, 없으면 Studio 실행 폴더의 tools/mediamtx/mediamtx.exe.</summary>
    public static string MediaMtxPath => Environment.GetEnvironmentVariable("DEVINNO_STUDIO_MEDIAMTX") is { Length: > 0 } p
        ? p : System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "mediamtx", "mediamtx.exe");

    /// <summary>ffmpeg: 환경 변수 DEVINNO_STUDIO_FFMPEG, 없으면 PATH 의 ffmpeg.exe. 못 찾으면 null.</summary>
    public static string? FfmpegPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("DEVINNO_STUDIO_FFMPEG") is { Length: > 0 } p)
                return p;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
            {
                var exe = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (dir.Length > 0 && File.Exists(exe))
                    return exe;
            }
            return null;
        }
    }

    public void Start(int width, int height, int fps)
    {
        if (!File.Exists(MediaMtxPath))
            throw new FileNotFoundException(Texts.T("emu.noMediaMtx", MediaMtxPath), MediaMtxPath);
        var ffmpeg = FfmpegPath ?? throw new FileNotFoundException(Texts.T("emu.noFfmpeg"));
        if (PortInUse(options.RtspPort))
            throw new IOException(Texts.T("emu.rtspPortInUse", options.RtspPort));

        // RTSP 만 켠 최소 설정
        _config = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"devinno-studio-mediamtx-{options.RtspPort}.yml");
        File.WriteAllText(_config, $"""
            logLevel: warn
            api: no
            metrics: no
            pprof: no
            playback: no
            rtmp: no
            hls: no
            webrtc: no
            srt: no
            moq: no
            rtsp: yes
            rtspTransports: [tcp]
            rtspAddress: {(options.LocalOnly ? "127.0.0.1" : "")}:{options.RtspPort}
            paths:
              all_others:
            """);
        _server = Launch(MediaMtxPath, [_config], redirectInput: false);
        var wait = Stopwatch.StartNew();
        while (!PortInUse(options.RtspPort))
        {
            if (_server.HasExited)
                throw new IOException(Texts.T("emu.serverExited", _server.ExitCode));
            if (wait.Elapsed > TimeSpan.FromSeconds(10))
                throw new IOException(Texts.T("emu.serverTimeout"));
            Thread.Sleep(50);
        }

        _ffmpeg = Launch(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{width}x{height}", "-r", $"{fps}", "-i", "-",
            "-an", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", $"{fps}", "-bf", "0",
            "-f", "rtsp", "-rtsp_transport", "tcp", $"rtsp://127.0.0.1:{options.RtspPort}/{options.Path}",
        ], redirectInput: true);
    }

    public void Write(Mat bgr)
    {
        if (_ffmpeg is not { } p || p.HasExited)
            throw new IOException(Texts.T("emu.ffmpegExited") + (_ffmpeg is { HasExited: true } e ? Texts.T("emu.exitCode", e.ExitCode) : ""));
        var bytes = new byte[bgr.Total() * bgr.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(bgr.Data, bytes, 0, bytes.Length);
        p.StandardInput.BaseStream.Write(bytes);
        p.StandardInput.BaseStream.Flush();
    }

    public void Dispose()
    {
        foreach (var p in new[] { _ffmpeg, _server })
        {
            if (p is null)
                continue;
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
            }
            p.Dispose();
        }
        (_ffmpeg, _server) = (null, null);
        try
        {
            if (_config is not null)
                File.Delete(_config);
        }
        catch (IOException)
        {
        }
    }

    private static Process Launch(string exe, IEnumerable<string> args, bool redirectInput)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = redirectInput };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new IOException(Texts.T("emu.cannotLaunch", exe));
    }

    private static bool PortInUse(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(200) && c.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
