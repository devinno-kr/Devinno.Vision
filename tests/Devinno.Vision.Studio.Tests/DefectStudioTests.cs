using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Tests;

// 불량 모델 (ONNX 검사) — 불량 폴더 · 학습(가짜 Python 스크립트) · 취소 · 실패 · ONNX 가져오기.
// 실제 PyTorch 학습은 환경 변수 DEVINNO_REAL_PYTHON (torch 가 든 python.exe) 이 있을 때만 돈다.
public class DefectStudioTests
{
    private static string TinyClassifier => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_classifier.onnx");

    private const string TinyInfo = """{ "Labels": ["good", "missinghole", "tabdefect"], "GoodLabel": "good", "InputWidth": 32, "InputHeight": 32, "Normalize": "ImageNet" }""";

    // PATH 의 python (가짜 학습 스크립트용 — torch 는 필요 없다)
    private static string SystemPython()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var exe = Path.Combine(dir.Trim(), "python.exe");
            if (dir.Length > 0 && File.Exists(exe))
                return exe;
        }
        Assert.Fail("이 테스트에는 PATH 의 python.exe 가 필요합니다");
        return "";
    }

    // 가짜 학습 스크립트: 진행 줄을 찍고, 작은 분류 모델을 결과로 둔다
    private static string FakeScript(TempDir dir, string mode = "ok") => Write(dir, "fake_train.py", $$"""
        import argparse, json, os, shutil, sys, time
        p = argparse.ArgumentParser()
        for a in ["--data", "--out", "--size", "--epochs", "--weights", "--good"]: p.add_argument(a)
        a = p.parse_args()
        labels = sorted(os.listdir(a.data))
        for e in range(1, 4):
            print(f"PROGRESS {e} 3 0.5 0.9", flush=True)
            if "{{mode}}" == "slow": time.sleep(3)
        if "{{mode}}" == "fail":
            print("boom: no torch", file=sys.stderr); sys.exit(3)
        os.makedirs(a.out, exist_ok=True)
        shutil.copy(r"{{TinyClassifier}}", os.path.join(a.out, "model.onnx"))
        json.dump({"Labels": labels, "GoodLabel": a.good, "InputWidth": 32, "InputHeight": 32, "Normalize": "ImageNet", "Accuracy": 0.9}, open(os.path.join(a.out, "model.json"), "w"))
        print("DONE 0.9")
        """);

    private static string Write(TempDir dir, string name, string text)
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllText(path, text);
        return path;
    }

    // Samples/Bracket 을 라벨 폴더로 (good · missinghole · tabdefect)
    private static string Defects(TempDir dir)
    {
        var root = dir.Sub("defects");
        foreach (var f in Directory.GetFiles(Samples.BracketDir, "*.bmp"))
        {
            var label = Path.GetFileNameWithoutExtension(f).Split('_')[1];
            if (label == "distancedefect")
                continue;
            Directory.CreateDirectory(Path.Combine(root, label));
            File.Copy(f, Path.Combine(root, label, Path.GetFileName(f)));
        }
        return root;
    }

    private static string Ok(CommandResult r)
    {
        Assert.True(r.Ok, r.Message);
        return r.Message;
    }

    // Bracket 로직 + ONNX 검사 하나 (부품 가운데)
    private static (StudioSession S, string Id) Prepared(TempDir dir, string? script = null)
    {
        var s = new StudioSession { PythonPath = SystemPython(), TrainScriptPath = script ?? FakeScript(dir) };
        BracketStudioTests.BuildBracket(s, dir.Sub("project"));
        var id = s.AddCheck("Onnx", "표면").Id!;
        Ok(s.SetRoi(id, VisionRoi.Rect(100, 150, 470, 250)));
        return (s, id);
    }

    [Fact]
    public void Defect_folder_needs_good_and_a_defect_kind()
    {
        using var dir = new TempDir();
        var (s, id) = Prepared(dir);
        var flat = dir.Sub("flat");
        File.Copy(Samples.Bracket("01_good.bmp"), Path.Combine(flat, "01_good.bmp"));

        Assert.Contains("하위 폴더 'good'", s.SetDefectFolder(id, flat).Message);
        Assert.Contains("ONNX 검사가 아닙니다", s.SetDefectFolder(s.Checks[1].Id, Defects(dir)).Message);
        var msg = Ok(s.SetDefectFolder(id, Path.Combine(dir.Path, "defects")));

        Assert.Contains("good 9장, missinghole 3장, tabdefect 3장", msg);
        Assert.Contains("주의: 적은 라벨", msg);
        Assert.DoesNotContain("주의: 테스트 폴더", msg);   // 같은 이미지의 복사본 — 경로가 달라 겹침으로 보지 않는다
    }

    [Fact]
    public async Task Training_runs_the_script_and_attaches_the_model()
    {
        using var dir = new TempDir();
        var (s, id) = Prepared(dir);
        Ok(s.SetDefectFolder(id, Defects(dir)));
        var stages = new List<string>();

        var msg = Ok(await s.TrainDefectModelAsync(id, new Relay<GoodModelProgress>(p => stages.Add(p.Stage))));

        Assert.Contains("표면 불량 모델 학습됨", msg);
        Assert.Contains("검증 정확도 90%", msg);
        Assert.Equal(["crop", "train"], stages.Distinct());
        var check = Assert.IsType<OnnxCheck>(s.GetCheck(id));
        Assert.StartsWith($"models/defect-{id}-", check.Model);
        Assert.Equal(["good", "missinghole", "tabdefect"], s.DefectLabels(id));
        Assert.Equal(0.9, s.DefectSummary(id)!.Accuracy);
        Assert.True(s.IsDirty);

        var run = await s.RunImageAsync(Samples.Bracket("02_missinghole.bmp"));
        Assert.True(run.Ok, run.Message);
        Assert.Contains(run.Values, v => v.Check == "표면" && v.Name == "표면 불량 확률");
    }

    [Fact]
    public async Task A_failing_script_reports_its_error()
    {
        using var dir = new TempDir();
        var (s, id) = Prepared(dir, null);
        s.TrainScriptPath = FakeScript(dir, "fail");
        Ok(s.SetDefectFolder(id, Defects(dir)));

        var r = await s.TrainDefectModelAsync(id);

        Assert.False(r.Ok);
        Assert.Contains("코드 3", r.Message);
        Assert.Contains("boom: no torch", r.Message);
        Assert.Equal("", ((OnnxCheck)s.GetCheck(id)!).Model);
    }

    [Fact]
    public async Task Cancel_stops_the_python_process_and_keeps_the_check_unchanged()
    {
        using var dir = new TempDir();
        var (s, id) = Prepared(dir);
        s.TrainScriptPath = FakeScript(dir, "slow");
        Ok(s.SetDefectFolder(id, Defects(dir)));
        var progress = new Relay<GoodModelProgress>(p =>
        {
            if (p.Stage == "train")
                s.CancelTraining();
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await s.TrainDefectModelAsync(id, progress);

        Assert.False(r.Ok);
        Assert.Contains("취소", r.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"{watch.Elapsed}");   // 9 초짜리 스크립트를 기다리지 않았다
        Assert.Equal("", ((OnnxCheck)s.GetCheck(id)!).Model);
        Assert.Empty(Directory.Exists(Path.Combine(s.Project!.Folder, "models")) ? Directory.GetDirectories(Path.Combine(s.Project.Folder, "models")) : []);
        Assert.False(s.IsTraining);
    }

    [Fact]
    public async Task Training_without_python_says_how_to_get_it()
    {
        using var dir = new TempDir();
        var (s, id) = Prepared(dir);
        s.PythonPath = Path.Combine(dir.Path, "no-python.exe");
        Ok(s.SetDefectFolder(id, Defects(dir)));

        var r = await s.TrainDefectModelAsync(id);

        Assert.False(r.Ok);
        Assert.Contains("Python 환경이 없습니다", r.Message);
        Assert.Contains("setup_python", r.Message);
    }

    [Fact]
    public async Task Import_needs_the_description_file_and_a_readable_model()
    {
        using var dir = new TempDir();
        var (s, id) = Prepared(dir);
        var models = dir.Sub("models");
        var onnx = Path.Combine(models, "surface.onnx");
        File.Copy(TinyClassifier, onnx);

        Assert.Contains("설명 파일이 없습니다", s.ImportOnnx(id, onnx).Message);

        File.WriteAllText(Path.ChangeExtension(onnx, ".json"), TinyInfo);
        Assert.Contains("라벨 good, missinghole, tabdefect", Ok(s.ImportOnnx(id, onnx)));
        Assert.True((await s.RunImageAsync(Samples.Bracket("01_good.bmp"))).Ok);

        var broken = Path.Combine(models, "broken.onnx");
        File.WriteAllText(broken, "not a model");
        File.WriteAllText(Path.ChangeExtension(broken, ".json"), TinyInfo);
        Assert.Contains("가져올 수 없습니다", s.ImportOnnx(id, broken).Message);
    }

    [Fact]
    public async Task Real_pytorch_training_when_available()
    {
        if (Environment.GetEnvironmentVariable("DEVINNO_REAL_PYTHON") is not { Length: > 0 } python)
            return;   // 실제 학습은 torch 가 있는 환경에서만 (사람이 켜서 돌린다)
        using var dir = new TempDir();
        var (s, id) = Prepared(dir, Path.Combine(AppContext.BaseDirectory, "train", "train_defect.py"));
        s.PythonPath = python;
        s.DefectEpochs = 3;
        s.DefectInputSize = 96;
        Ok(s.SetDefectFolder(id, Defects(dir)));

        var msg = Ok(await s.TrainDefectModelAsync(id));

        Assert.Contains("불량 모델 학습됨", msg);
        Assert.Equal(["good", "missinghole", "tabdefect"], s.DefectLabels(id));
        Assert.True((await s.RunImageAsync(Samples.Bracket("02_missinghole.bmp"))).Ok);
    }

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
