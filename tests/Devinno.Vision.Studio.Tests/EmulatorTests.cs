using System.Net.Sockets;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;
using OpenCvSharp;

namespace Devinno.Vision.Studio.Tests;

// 테스트의 가상 카메라는 모두 127.0.0.1 에서만 연다 — Windows 방화벽 허용 창이 뜨지 않게 (2026-11-11 사용자 보고)
// 가상 카메라 — Modbus 핸드셰이크 (D100 ← 1 → D100 0 · D0 1 → 다음 장 → D0 0). 프레임은 메모리로 받는다.
// 실제 RTSP(ffmpeg + MediaMTX)는 Real_rtsp_stream_follows_the_handshake 가 돈다 (둘 다 있을 때만).
public class EmulatorTests
{
    private static int _port = 15020;

    private static int NextPort() => Interlocked.Increment(ref _port);

    // 밝기가 서로 다른 이미지 3장 (파일 이름 라벨)
    private static string Images(TempDir dir)
    {
        var folder = dir.Sub("frames");
        foreach (var (name, v) in new[] { ("01_good.png", 40), ("02_good.png", 120), ("03_good.png", 220) })
            using (var m = new Mat(120, 160, MatType.CV_8UC3, new Scalar(v, v, v)))
                m.SaveImage(Path.Combine(folder, name));
        return folder;
    }

    private sealed class MemorySink : IFrameSink
    {
        public List<double> Means { get; } = [];
        public (int W, int H, int Fps) Started { get; private set; }

        public void Start(int width, int height, int fps) => Started = (width, height, fps);

        public void Write(Mat bgr)
        {
            lock (Means)
                Means.Add(Cv2.Mean(bgr).Val0);
        }

        public double Last { get { lock (Means) return Means[^1]; } }

        public void Dispose()
        {
        }
    }

    // 아주 작은 Modbus TCP 마스터 (FC03 읽기 · FC06 쓰기)
    private sealed class Master : IDisposable
    {
        private readonly TcpClient _c = new();
        private ushort _tid;

        public Master(int port) => _c.Connect("127.0.0.1", port);

        public int Read(int address) => Exchange(3, address, 1)[9] << 8 | Exchange(3, address, 1)[10];

        public void Write(int address, int value) => Exchange(6, address, value);

        private byte[] Exchange(byte fc, int address, int value)
        {
            var t = ++_tid;
            byte[] req = [(byte)(t >> 8), (byte)t, 0, 0, 0, 6, 1, fc, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value];
            var s = _c.GetStream();
            s.Write(req);
            var res = new byte[256];
            var n = s.Read(res);
            Assert.True(n >= 9 && res[7] == fc, $"Modbus 응답 오류: {Convert.ToHexString(res, 0, n)}");
            return res[..n];
        }

        public void Dispose() => _c.Dispose();
    }

    private static (StudioSession S, MemorySink Sink, EmulatorOptions O) Started(TempDir dir, int slideMs = 200, int settleMs = 200)
    {
        var sink = new MemorySink();
        var s = new StudioSession { FrameSink = _ => sink };
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);
        Assert.True(s.AddTestSet(Images(dir), LabelRule.FileName).Ok);
        var o = new EmulatorOptions(NextPort(), NextPort(), Fps: 20, SlideMs: slideMs, SettleMs: settleMs, LocalOnly: true);
        var r = s.StartEmulator(0, o);
        Assert.True(r.Ok, r.Message);
        return (s, sink, o);
    }

    private static void Until(Func<bool> done, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "기다린 상태가 되지 않았습니다");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Writing_D100_slides_to_the_next_image_with_the_D0_handshake()
    {
        using var dir = new TempDir();
        var (s, sink, o) = Started(dir);
        try
        {
            using var m = new Master(o.ModbusPort);
            Until(() => sink.Means.Count > 3);
            Assert.Equal(40, sink.Last, 1);
            Assert.Equal(0, m.Read(0));   // D0 정지 (검사해도 됨)

            m.Write(100, 1);
            Assert.Equal(1, m.Read(0));     // 쓰기 응답을 받았을 때 이미 넘기는 중
            Assert.Equal(0, m.Read(100));   // D100 은 Studio 가 0 으로

            Until(() => m.Read(0) == 0);
            Assert.Equal(120, sink.Last, 1);   // D0 = 0 이면 새 이미지가 나가고 있다
            Assert.Equal(1, s.EmulatorStatus!.Index);
            lock (sink.Means)
                Assert.Contains(sink.Means, v => v > 45 && v < 115);   // 밀려 들어오는 중간 프레임
        }
        finally
        {
            s.StopEmulator();
        }
    }

    [Fact]
    public void A_request_while_sliding_is_kept_and_the_last_image_wraps_to_the_first()
    {
        using var dir = new TempDir();
        var (s, sink, o) = Started(dir, slideMs: 300, settleMs: 300);
        try
        {
            using var m = new Master(o.ModbusPort);
            Until(() => sink.Means.Count > 1);
            m.Write(100, 1);
            m.Write(100, 1);   // 넘기는 중에 한 번 더

            Until(() => m.Read(0) == 0);
            Assert.Equal(2, s.EmulatorStatus!.Index);   // 두 장 넘어갔다 (중간에 D0 은 0 으로 내려가지 않음)
            Assert.Equal(220, sink.Last, 1);

            m.Write(100, 1);
            Until(() => m.Read(0) == 0);
            Assert.Equal(0, s.EmulatorStatus!.Index);   // 마지막 다음은 처음
            Assert.Equal(40, sink.Last, 1);
        }
        finally
        {
            s.StopEmulator();
        }
    }

    [Fact]
    public void Stopping_closes_the_modbus_port_and_cannot_start_twice()
    {
        using var dir = new TempDir();
        var (s, _, o) = Started(dir);

        Assert.False(s.StartEmulator(0, o).Ok);
        Assert.True(s.StopEmulator().Ok);

        Assert.Null(s.EmulatorStatus);
        Assert.ThrowsAny<SocketException>(() => new Master(o.ModbusPort));
        Assert.False(s.StopEmulator().Ok);
    }

    [Fact]
    public void Missing_tools_or_test_folder_say_so()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);

        Assert.Contains("테스트 폴더가 없습니다", s.StartEmulator(0).Message);

        Assert.True(s.AddTestSet(Images(dir), LabelRule.FileName).Ok);
        var saved = Environment.GetEnvironmentVariable("DEVINNO_STUDIO_MEDIAMTX");
        try
        {
            Environment.SetEnvironmentVariable("DEVINNO_STUDIO_MEDIAMTX", Path.Combine(dir.Path, "none.exe"));
            var r = s.StartEmulator(0, new EmulatorOptions(NextPort(), NextPort(), LocalOnly: true));
            Assert.False(r.Ok);
            Assert.Contains("MediaMTX", r.Message);
            Assert.Null(s.EmulatorStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVINNO_STUDIO_MEDIAMTX", saved);
        }
    }

    [Fact]
    public async Task Real_rtsp_stream_follows_the_handshake()
    {
        if (!File.Exists(RtspSink.MediaMtxPath) || RtspSink.FfmpegPath is null)
            return;   // ffmpeg · MediaMTX 가 있는 PC 에서만
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);
        Assert.True(s.AddTestSet(Images(dir), LabelRule.FileName).Ok);
        var o = new EmulatorOptions(NextPort(), NextPort(), Path: "t", Fps: 10, LocalOnly: true);
        var start = s.StartEmulator(0, o);
        Assert.True(start.Ok, start.Message);
        try
        {
            await using var cam = await RtspCamera.OpenAsync($"rtsp://127.0.0.1:{o.RtspPort}/t", TimeSpan.FromSeconds(15));
            using (var f = await cam.Grab(CancellationToken.None))
                Assert.InRange(Cv2.Mean(f.Mat).Val0, 32, 48);   // H.264 라 정확히 같지는 않다

            using var m = new Master(o.ModbusPort);
            m.Write(100, 1);
            Assert.Equal(1, m.Read(0));
            Until(() => m.Read(0) == 0, 10000);
            using (var f = await cam.Grab(CancellationToken.None))
                Assert.InRange(Cv2.Mean(f.Mat).Val0, 112, 128);   // D0 = 0 뒤에 잡은 프레임은 새 이미지
        }
        finally
        {
            s.StopEmulator();
        }
    }
}
