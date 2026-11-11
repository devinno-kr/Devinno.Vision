using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devinno.Vision.Recipes;

namespace Devinno.Vision.Studio.Session;

/// <summary>불량 모델 학습 결과 요약 (모델 폴더의 train.json).</summary>
/// <param name="Folder">학습한 불량 폴더.</param>
/// <param name="Images">라벨별 이미지 수 (잘라 넘긴 것).</param>
/// <param name="Accuracy">검증 정확도 (0~1, 검증 이미지가 없으면 null).</param>
/// <param name="Skipped">뺀 이미지 (파일 이름: 이유).</param>
/// <param name="Trained">학습한 시각.</param>
public sealed record DefectTrainingSummary(string Folder, IReadOnlyDictionary<string, int> Images, double? Accuracy, IReadOnlyList<string> Skipped, DateTime Trained);

// 불량 모델 (ONNX 검사) — 불량 폴더 · 학습(Python) · ONNX 가져오기 · Python 환경
public sealed partial class StudioSession
{
    internal const string GoodLabel = "good";

    private string? _pythonPath;

    /// <summary>학습에 쓰는 Python. 기본: 환경 변수 DEVINNO_STUDIO_PYTHON, 없으면 Studio 가 만든 환경 (%LOCALAPPDATA%\Devinno.Vision.Studio\python).</summary>
    public string PythonPath
    {
        get => _pythonPath ?? (Environment.GetEnvironmentVariable("DEVINNO_STUDIO_PYTHON") is { Length: > 0 } env ? env : ManagedPython);
        internal set => _pythonPath = value;
    }

    /// <summary>Studio 가 만드는 Python 환경의 python.exe.</summary>
    public static string ManagedPython => Path.Combine(ManagedPythonFolder, "Scripts", "python.exe");

    private static string ManagedPythonFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Devinno.Vision.Studio", "python");

    /// <summary>Python 환경이 있는가.</summary>
    public bool HasPython => File.Exists(PythonPath);

    /// <summary>불량 학습 스크립트 (Studio 실행 폴더의 train/train_defect.py).</summary>
    internal string TrainScriptPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "train", "train_defect.py");

    /// <summary>ImageNet ResNet18 가중치 (있으면 쓰고, 없으면 스크립트가 내려받기를 시도한다).</summary>
    internal string WeightsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "models", "resnet18-f37072fd.pth");

    internal int DefectEpochs { get; set; } = 15;
    internal int DefectInputSize { get; set; } = 224;

    // ── 상태 ─────────────────────────────────────────────

    /// <summary>ONNX 검사의 불량 폴더 (없으면 null).</summary>
    public string? DefectFolder(string id) { lock (_gate) return _goods.Defects.GetValueOrDefault(id); }

    /// <summary>ONNX 검사 모델의 학습 요약. 없으면 null.</summary>
    public DefectTrainingSummary? DefectSummary(string id)
    {
        string? file;
        lock (_gate)
            file = _project is { } p && Find(id) is OnnxCheck { Model.Length: > 0 } o ? Path.Combine(p.Folder, o.Model, TrainFile) : null;
        if (file is null || !File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize<DefectTrainingSummary>(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>ONNX 검사 모델의 라벨 (model.json). 없으면 빈 목록.</summary>
    public IReadOnlyList<string> DefectLabels(string id)
    {
        string? file;
        lock (_gate)
            file = _project is { } p && Find(id) is OnnxCheck { Model.Length: > 0 } o ? Path.Combine(p.Folder, o.Model, DefectModel.InfoFile) : null;
        try
        {
            return file is not null && File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["Labels"] is JsonArray a
                ? [.. a.Select(x => x?.GetValue<string>() ?? "")]
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ── 명령 ─────────────────────────────────────────────

    /// <summary>ONNX 검사의 불량 폴더: 하위 폴더 이름이 라벨 (good = 양품, 나머지 = 불량 종류).</summary>
    public CommandResult SetDefectFolder(string id, string folder) => Change(() =>
    {
        if (Find(id) is not OnnxCheck o)
            return CommandResult.Fail($"ONNX 검사가 아닙니다: {id}");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return CommandResult.Fail($"폴더가 없습니다: {folder}");
        var full = Path.GetFullPath(folder);
        if (Overlaps(full, _project!.Folder))
            return CommandResult.Fail($"프로젝트 폴더(그 안이나 위 폴더 포함)는 쓸 수 없습니다: {full}");
        var labels = LabelsIn(full);
        if (!labels.ContainsKey(GoodLabel) || labels.Count < 2)
            return CommandResult.Fail($"하위 폴더 '{GoodLabel}'(양품)와 불량 종류 폴더가 1개 이상 있어야 합니다 (있는 것: {(labels.Count == 0 ? "없음" : string.Join(", ", labels.Keys))})");
        _goods.Defects[id] = full;
        if (SaveGoodSet() is { } error)
            return CommandResult.Fail(error);
        var few = labels.Where(kv => kv.Value < 10).Select(kv => $"{kv.Key} {kv.Value}장").ToList();
        var warnings = string.Concat(_project.TestSets.Select(t => OverlapWarning(full, t.Folder, "테스트 폴더")));
        return CommandResult.Done($"{o.Name} 불량 폴더: {full} ({string.Join(", ", labels.Select(kv => $"{kv.Key} {kv.Value}장"))})" +
                                  (few.Count > 0 ? $" — 주의: 적은 라벨이 있습니다 ({string.Join(", ", few)})" : "") + warnings);
    }, markDirty: false);

    /// <summary>
    /// 불량 폴더로 ONNX 검사의 모델을 학습한다: 이미지마다 정렬 · ROI 로 잘라 Python(PyTorch) 학습 스크립트에 넘기고, 나온 model.onnx 를 이 검사에 건다.
    /// 오래 걸린다 — 진행률, <see cref="CancelTraining"/>. 학습 중에는 다른 명령을 막는다.
    /// </summary>
    public async Task<CommandResult> TrainDefectModelAsync(string id, IProgress<GoodModelProgress>? progress = null)
    {
        VisionRecipe p;
        OnnxCheck check;
        string folder;
        string python, script, weights;
        int epochs, size;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_training is not null)
                return CommandResult.Fail("이미 학습하는 중입니다");
            if (_project is not { } cur)
                return CommandResult.Fail(NoProject);
            if (Find(id) is not OnnxCheck o)
                return CommandResult.Fail($"ONNX 검사가 아닙니다: {id}");
            if (o.Roi.IsEmpty)
                return CommandResult.Fail($"{o.Name}: ROI 를 먼저 그리세요");
            if (_goods.Defects.GetValueOrDefault(id) is not { } f || !Directory.Exists(f))
                return CommandResult.Fail($"{o.Name}: 불량 폴더를 먼저 지정하세요 (set_defect_folder)");
            if (!HasPython)
                return CommandResult.Fail($"Python 환경이 없습니다: {PythonPath} — [Python 준비] (setup_python) 로 만드세요 (PyTorch 를 내려받습니다)");
            if (!File.Exists(TrainScriptPath))
                return CommandResult.Fail($"학습 스크립트가 없습니다: {TrainScriptPath}");
            (p, check, folder) = (cur, o, f);
            (python, script, weights, epochs, size) = (PythonPath, TrainScriptPath, WeightsPath, DefectEpochs, DefectInputSize);
            _training = cts = new CancellationTokenSource();
            _trainProgress = null;
        }
        ProjectChanged?.Invoke();
        var data = Path.Combine(Path.GetTempPath(), "devinno-studio-" + Guid.NewGuid().ToString("N")[..8]);
        var rel = $"models/defect-{check.Id}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var outDir = Path.Combine(p.Folder, rel);
        try
        {
            void Report(GoodModelProgress x)
            {
                lock (_gate)
                    _trainProgress = x;
                progress?.Report(x);
                ProjectChanged?.Invoke();
            }

            // 1. 정렬 + ROI 로 자르기 (실행 때와 같게)
            var labels = LabelsIn(folder);
            var files = labels.Keys.SelectMany(l => ImagesIn(Path.Combine(folder, l)).Select(f => (Label: l, File: f))).ToList();
            var skipped = new List<string>();
            var counts = new Dictionary<string, int>();
            for (var i = 0; i < files.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var (label, file) = files[i];
                try
                {
                    using var img = VisionImage.Load(file);
                    using var crop = await p.CropAsync(img, check.Roi, cts.Token).ConfigureAwait(false);
                    var dir = Path.Combine(data, label);
                    Directory.CreateDirectory(dir);
                    crop.Save(Path.Combine(dir, $"{i:D5}.png"));
                    counts[label] = counts.GetValueOrDefault(label) + 1;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or OpenCvSharp.OpenCVException || ex is InvalidOperationException and not ObjectDisposedException)
                {
                    skipped.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
                Report(new GoodModelProgress("crop", i + 1, files.Count));
            }
            if (!counts.ContainsKey(GoodLabel) || counts.Count < 2)
                return CommandResult.Fail($"자를 수 있는 이미지가 모자랍니다 ({string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}장"))}). 뺀 이미지: {string.Join("; ", skipped.Take(3))}");

            // 2. Python 학습
            var args = new List<string> { script, "--data", data, "--out", outDir, "--size", size.ToString(CultureInfo.InvariantCulture),
                                          "--epochs", epochs.ToString(CultureInfo.InvariantCulture), "--good", GoodLabel };
            if (File.Exists(weights))
                args.AddRange(["--weights", weights]);
            var (code, errors) = await RunPythonAsync(python, args, line =>
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 3 && t[0] == "PROGRESS" && int.TryParse(t[1], out var e) && int.TryParse(t[2], out var n))
                    Report(new GoodModelProgress("train", e, n));
            }, cts.Token).ConfigureAwait(false);
            if (code != 0)
                return CommandResult.Fail($"학습 스크립트가 실패했습니다 (코드 {code}): {string.Join(" / ", errors.TakeLast(3))}");

            // 3. 결과 확인 · 요약 · 검사에 걸기
            double? accuracy;
            using (var m = DefectModel.Load(outDir))
                accuracy = ReadAccuracy(outDir);
            var summary = new DefectTrainingSummary(folder, counts, accuracy, skipped, DateTime.Now);
            File.WriteAllText(Path.Combine(outDir, TrainFile), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            lock (_gate)
            {
                check.Model = rel;
                _revision++;
                _dirty = true;
            }
            return CommandResult.Done($"{check.Name} 불량 모델 학습됨: {string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}장"))}" +
                                      (accuracy is { } a ? $" · 검증 정확도 {a * 100:0.#}%" : " · 검증 이미지 없음") +
                                      (skipped.Count > 0 ? $" · 뺀 이미지 {skipped.Count}장" : ""));
        }
        catch (OperationCanceledException)
        {
            TryDelete(outDir);
            return CommandResult.Fail("학습을 취소했습니다 (모델은 그대로입니다)");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDelete(outDir);
            return CommandResult.Fail($"학습할 수 없습니다: {ex.Message}");
        }
        finally
        {
            TryDelete(data);
            lock (_gate)
            {
                _training = null;
                _trainProgress = null;
            }
            cts.Dispose();
            ProjectChanged?.Invoke();
        }
    }

    /// <summary>
    /// 밖에서 만든 ONNX 를 이 검사의 모델로 가져온다. 같은 폴더에 설명 파일(파일이름.json 또는 model.json)이 있어야 한다:
    /// { "Labels": ["good", "scratch"], "GoodLabel": "good", "InputWidth": 224, "InputHeight": 224, "Normalize": "ImageNet" }.
    /// </summary>
    public CommandResult ImportOnnx(string id, string file) => Change(() =>
    {
        var p = _project!;
        if (Find(id) is not OnnxCheck o)
            return CommandResult.Fail($"ONNX 검사가 아닙니다: {id}");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) || !file.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($".onnx 파일이 없습니다: {file}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var info = new[] { Path.ChangeExtension(file, ".json"), Path.Combine(dir, DefectModel.InfoFile) }.FirstOrDefault(File.Exists);
        if (info is null)
            return CommandResult.Fail($"설명 파일이 없습니다: {Path.GetFileNameWithoutExtension(file)}.json 또는 model.json 을 같은 폴더에 두세요 — " +
                                      """{ "Labels": ["good", "scratch"], "GoodLabel": "good", "InputWidth": 224, "InputHeight": 224, "Normalize": "ImageNet" } (출력은 라벨마다 점수 하나 [1, N])""");
        var rel = $"models/defect-{o.Id}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var outDir = Path.Combine(p.Folder, rel);
        try
        {
            Directory.CreateDirectory(outDir);
            File.Copy(file, Path.Combine(outDir, DefectModel.ModelFile));
            File.Copy(info, Path.Combine(outDir, DefectModel.InfoFile));
            using var m = DefectModel.Load(outDir);   // 읽히는지 확인
            o.Model = rel;
            return CommandResult.Done($"{o.Name} 모델을 가져왔습니다: {Path.GetFileName(file)} (라벨 {string.Join(", ", m.Labels)}, 입력 {m.InputWidth}×{m.InputHeight})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException || ex.GetType().Name == "OnnxRuntimeException")
        {
            TryDelete(outDir);
            return CommandResult.Fail($"가져올 수 없습니다: {ex.Message}");
        }
    });

    /// <summary>
    /// Studio 의 Python 환경을 만든다 (%LOCALAPPDATA%\Devinno.Vision.Studio\python): venv + PyTorch(CPU) · onnx · numpy · pillow 를 내려받아 설치한다 (수백 MB, 몇 분).
    /// 이 PC 의 Python 3 (py 또는 python) 이 필요하다.
    /// </summary>
    public async Task<CommandResult> SetupPythonAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var baseline = FindSystemPython();
        if (baseline is null)
            return CommandResult.Fail("이 PC 에 Python 3 이 없습니다 — python.org 에서 설치한 뒤 다시 누르세요");
        var venv = ManagedPythonFolder;
        var steps = new List<(string Exe, List<string> Args, string What)>();
        if (!File.Exists(ManagedPython))
            steps.Add((baseline.Value.Exe, [.. baseline.Value.Prefix, "-m", "venv", venv], "Python 환경 만들기"));
        steps.Add((ManagedPython, ["-m", "pip", "install", "--upgrade", "pip"], "pip 올리기"));
        steps.Add((ManagedPython, ["-m", "pip", "install", "torch", "--index-url", "https://download.pytorch.org/whl/cpu"], "PyTorch(CPU) 설치 — 수백 MB"));
        steps.Add((ManagedPython, ["-m", "pip", "install", "onnx", "numpy", "pillow"], "onnx · numpy · pillow 설치"));
        foreach (var (exe, args, what) in steps)
        {
            progress?.Report(what);
            var (code, errors) = await RunPythonAsync(exe, args, line => progress?.Report(line), ct).ConfigureAwait(false);
            if (code != 0)
                return CommandResult.Fail($"{what} 실패 (코드 {code}): {string.Join(" / ", errors.TakeLast(3))}");
        }
        _pythonPath = null;
        return CommandResult.Done($"Python 환경을 만들었습니다: {ManagedPython}");
    }

    // ── 내부 ─────────────────────────────────────────────

    // 하위 폴더(라벨) → 이미지 수
    private static Dictionary<string, int> LabelsIn(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .Select(d => (Label: Path.GetFileName(d), Count: ImagesIn(d).Count))
                .Where(x => x.Count > 0)
                .OrderBy(x => x.Label, StringComparer.Ordinal)
                .ToDictionary(x => x.Label, x => x.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static double? ReadAccuracy(string dir)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, DefectModel.InfoFile)))?["Accuracy"]?.GetValue<double?>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static (string Exe, string[] Prefix)? FindSystemPython()
    {
        foreach (var (exe, prefix) in new[] { ("py", new[] { "-3" }), ("python", Array.Empty<string>()) })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--version" }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                if (p is null)
                    continue;
                p.WaitForExit(10_000);
                if (p.HasExited && p.ExitCode == 0)
                    return (exe, prefix);
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        return null;
    }

    // 프로세스를 돌려 줄마다 넘긴다. 취소하면 프로세스 트리를 끝낸다. (종료 코드, 표준 오류 끝 줄들)
    internal static async Task<(int Code, List<string> Errors)> RunPythonAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        using var proc = new Process { StartInfo = psi };
        var errors = new List<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data is { } d) onLine(d); };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } d)
                lock (errors)
                {
                    errors.Add(d);
                    if (errors.Count > 50)
                        errors.RemoveAt(0);
                }
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using (ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (errors)
            return (proc.ExitCode, [.. errors]);
    }
}
