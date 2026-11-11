namespace Devinno.Vision.Studio.Session;

// 카메라 에뮬레이터 — Studio 가 가상 카메라가 된다 (RTSP 송출 + Modbus TCP 로 한 장씩)
public sealed partial class StudioSession
{
    private CameraEmulator? _emulator;

    /// <summary>테스트 · 내부용: 프레임을 받는 곳을 바꾼다 (기본 RTSP).</summary>
    internal Func<EmulatorOptions, IFrameSink> FrameSink { get; set; } = o => new RtspSink(o);

    /// <summary>에뮬레이터 상태. 돌고 있지 않으면 null.</summary>
    public EmulatorStatus? EmulatorStatus { get { lock (_gate) return _emulator?.Status; } }

    /// <summary>
    /// 테스트 폴더의 이미지를 가상 카메라로 내보낸다. Modbus: D100 ← 1 이면 다음 장 (D100 → 0, D0 = 1 넘기는 중 → 끝나면 0 — HMI 는 D0 = 0 일 때 검사).
    /// </summary>
    public CommandResult StartEmulator(int setIndex, EmulatorOptions? options = null)
    {
        IReadOnlyList<string> files;
        lock (_gate)
        {
            if (_project is null)
                return CommandResult.Fail(NoProject);
            if (_emulator is not null)
                return CommandResult.Fail("에뮬레이터가 이미 돌고 있습니다 (stop_emulator)");
            if (setIndex < 0 || setIndex >= _project.TestSets.Count)
                return CommandResult.Fail(_project.TestSets.Count == 0 ? "테스트 폴더가 없습니다 (add_test_set)" : $"테스트 폴더 번호는 0 ~ {_project.TestSets.Count - 1} 입니다 (받은 값 {setIndex})");
        }
        files = [.. TestImages(setIndex).Select(i => i.File)];
        if (files.Count == 0)
            return CommandResult.Fail("테스트 폴더에 이미지가 없습니다");
        var o = options ?? new EmulatorOptions();
        CameraEmulator emulator;
        try
        {
            emulator = new CameraEmulator(files, o, FrameSink(o));
            emulator.Changed += () => ProjectChanged?.Invoke();   // 화면이 몇 번째 장인지 다시 그리게
            emulator.Start();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Fail($"에뮬레이터를 시작할 수 없습니다: {ex.Message}");
        }
        lock (_gate)
        {
            if (_emulator is not null)
            {
                emulator.Dispose();
                return CommandResult.Fail("에뮬레이터가 이미 돌고 있습니다");
            }
            _emulator = emulator;
        }
        ProjectChanged?.Invoke();
        return CommandResult.Done($"가상 카메라: {emulator.Url} · Modbus TCP {o.ModbusPort} (D100 ← 1 다음 장, D0 1 = 넘기는 중 · 0 = 정지) · 이미지 {files.Count}장");
    }

    /// <summary>에뮬레이터를 멈춘다 (RTSP · Modbus 를 닫는다).</summary>
    public CommandResult StopEmulator()
    {
        CameraEmulator? e;
        lock (_gate)
        {
            e = _emulator;
            _emulator = null;
        }
        if (e is null)
            return CommandResult.Fail("에뮬레이터가 돌고 있지 않습니다");
        e.Dispose();
        ProjectChanged?.Invoke();
        return CommandResult.Done("가상 카메라를 멈췄습니다");
    }
}
