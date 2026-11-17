using System.IO.Compression;
using System.Security.Cryptography;

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
                return CommandResult.Fail(Texts.T("emu.alreadyRunningStop"));
            if (setIndex < 0 || setIndex >= _project.TestSets.Count)
                return CommandResult.Fail(_project.TestSets.Count == 0 ? Texts.T("emu.noTestSets") : Texts.T("emu.setIndexRange", _project.TestSets.Count - 1, setIndex));
        }
        files = [.. TestImages(setIndex).Select(i => i.File)];
        if (files.Count == 0)
            return CommandResult.Fail(Texts.T("emu.noTestImages"));
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
            return CommandResult.Fail(Texts.T("emu.startFailed", ex.Message));
        }
        lock (_gate)
        {
            if (_emulator is not null)
            {
                emulator.Dispose();
                return CommandResult.Fail(Texts.T("emu.alreadyRunning"));
            }
            _emulator = emulator;
        }
        ProjectChanged?.Invoke();
        return CommandResult.Done(Texts.T("emu.started", emulator.Url, o.ModbusPort, files.Count));
    }

    // ── MediaMTX 설치 ────────────────────────────────────
    // 가상 카메라의 RTSP 서버. Studio 배포 파일에는 넣지 않고(크기), 쓰는 사람이 [설치] 를 누르면 고정한 버전을 받아 온다.

    /// <summary>받아 오는 MediaMTX 버전 (MIT). 버전을 올리면 <see cref="MediaMtxSha256"/> 도 같이 바꾼다.</summary>
    public const string MediaMtxVersion = "v1.21.3";

    /// <summary>받을 파일 (GitHub 릴리스의 Windows x64 zip, 약 29 MB).</summary>
    public static string MediaMtxUrl => $"https://github.com/bluenviron/mediamtx/releases/download/{MediaMtxVersion}/mediamtx_{MediaMtxVersion}_windows_amd64.zip";

    /// <summary>받은 zip 의 SHA-256 (릴리스 페이지의 값). 다르면 설치하지 않는다. 테스트가 바꾼다.</summary>
    internal string MediaMtxSha256 { get; set; } = "b2f5246b07cb72961df03b8d5e8cad43dc657bcf3f49a87bb031bdf22721af74";

    /// <summary>zip 을 파일로 내려받는다 (파일 경로, 취소). 테스트가 바꾼다.</summary>
    internal Func<string, CancellationToken, Task> DownloadMediaMtx { get; set; } = async (file, ct) =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(MediaMtxUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var to = File.Create(file);
        await response.Content.CopyToAsync(to, ct).ConfigureAwait(false);
    };

    /// <summary>MediaMTX 가 있는가 (가상 카메라를 시작할 수 있는가 — ffmpeg 는 따로).</summary>
    public bool HasMediaMtx => File.Exists(RtspSink.MediaMtxPath);

    /// <summary>
    /// MediaMTX 를 내려받아 <see cref="RtspSink.MediaMtxPath"/> 자리에 둔다 (mediamtx.exe 와 LICENSE). 받은 파일의 SHA-256 이 고정한 값과 같을 때만 설치한다.
    /// 이미 있으면 아무것도 하지 않는다.
    /// </summary>
    public async Task<CommandResult> SetupMediaMtxAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var exe = RtspSink.MediaMtxPath;
        if (File.Exists(exe))
            return CommandResult.Done(Texts.T("emu.mediaMtxAlready", exe));
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var zip = Path.Combine(Path.GetTempPath(), $"mediamtx-{Guid.NewGuid():N}.zip");
        var part = exe + ".part";
        try
        {
            progress?.Report(Texts.T("emu.mediaMtxDownloading", MediaMtxVersion));
            await DownloadMediaMtx(zip, ct).ConfigureAwait(false);
            string hash;
            await using (var read = File.OpenRead(zip))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct).ConfigureAwait(false));
            if (!string.Equals(hash, MediaMtxSha256, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail(Texts.T("emu.mediaMtxBadHash", hash, MediaMtxSha256));

            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.GetEntry("mediamtx.exe");
            if (entry is null)
                return CommandResult.Fail(Texts.T("emu.mediaMtxFailed", "mediamtx.exe"));
            Directory.CreateDirectory(dir);
            archive.GetEntry("LICENSE")?.ExtractToFile(Path.Combine(dir, "LICENSE"), overwrite: true);
            entry.ExtractToFile(part, overwrite: true);   // 다 풀린 뒤에 제 이름으로 — 풀다 만 exe 가 남지 않게
            File.Move(part, exe);
            ProjectChanged?.Invoke();
            return CommandResult.Done(Texts.T("emu.mediaMtxReady", MediaMtxVersion, exe));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CommandResult.Fail(Texts.T("emu.mediaMtxFailed", "canceled"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            return CommandResult.Fail(Texts.T("emu.mediaMtxFailed", ex.Message));
        }
        finally
        {
            foreach (var f in new[] { zip, part })
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
            return CommandResult.Fail(Texts.T("emu.notRunning"));
        e.Dispose();
        ProjectChanged?.Invoke();
        return CommandResult.Done(Texts.T("emu.stopped"));
    }
}
