using System.IO.Compression;
using System.Text.Json.Nodes;
using Devinno.Vision;
using Devinno.Vision.Recipes;

// 레시피 패키지 (.dvpkg) — 묶기 · 열기 · 확인
public class PackageTests
{
    private static string Plate => Path.Combine(AppContext.BaseDirectory, "Recipes", "Plate");

    private static string Tiny => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_features.onnx");

    private static VisionRecipe CopyPlate(TempDir dir)
    {
        File.Copy(Path.Combine(Plate, VisionRecipe.FileName), Path.Combine(dir.Path, VisionRecipe.FileName));
        Directory.CreateDirectory(Path.Combine(dir.Path, "images"));
        File.Copy(Path.Combine(Plate, "images", "base.bmp"), Path.Combine(dir.Path, "images", "base.bmp"));
        return VisionRecipe.Load(dir.Path);
    }

    [Fact]
    public async Task Packed_recipe_loads_without_the_folder_and_judges_all_18()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "plate.dvpkg");
        using (var recipe = VisionRecipe.Load(Plate))
            Assert.Equal(2, recipe.Pack(file));   // studio.json · base.bmp (manifest 는 세지 않음)

        using var zip = ZipFile.OpenRead(file);
        Assert.Equal([VisionRecipe.FileName, "images/base.bmp", VisionRecipe.ManifestFile], zip.Entries.Select(e => e.FullName));
        var packedJson = JsonNode.Parse(zip.GetEntry(VisionRecipe.FileName)!.Open())!;
        Assert.Empty(packedJson["TestSets"]!.AsArray());   // 엔지니어 PC 경로는 빼고 묶는다
        var manifest = JsonNode.Parse(zip.GetEntry(VisionRecipe.ManifestFile)!.Open())!;
        Assert.Equal("플레이트", manifest["Name"]!.GetValue<string>());
        Assert.Equal(64, manifest["Files"]![0]!["Sha256"]!.GetValue<string>().Length);
        zip.Dispose();

        using var loaded = VisionRecipe.Load(file);
        Assert.Equal(Path.GetFullPath(file), loaded.PackagePath);
        Assert.NotEqual(Plate, loaded.Folder);
        var job = loaded.CreateJob();
        var right = 0;
        foreach (var f in Directory.GetFiles(PlateInspection.SampleDir, "*.bmp"))
        {
            using var img = VisionImage.Load(f);
            using var r = await job.RunAsync(img);
            var good = Path.GetFileName(f).Contains("_good");
            if (good == (r.State == VisionState.Ok) && r.State != VisionState.Error)
                right++;
        }
        Assert.Equal(18, right);
    }

    [Fact]
    public async Task Models_are_packed_without_training_files_and_load_back()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "out", "plate.dvpkg");
        string modelPath;
        using (var recipe = CopyPlate(dir))
        {
            var t = await recipe.TrainGoodModelAsync(new GoodModelTraining
            {
                Images = Directory.GetFiles(PlateInspection.SampleDir, "*_good.bmp"), Features = Tiny, Roi = VisionRoi.Rect(100, 150, 470, 250), InputSize = 64, Representatives = 8,
            });
            modelPath = t.Model.Path;
            File.WriteAllText(Path.Combine(dir.Path, modelPath, "train.json"), "{}");   // Studio 의 학습 요약 (PC 경로) — 빠져야 한다
            DefectModelTests.ModelFolder(dir.Path, "models/defect-x");
            recipe.Checks.Add(new OnnxCheck { Name = "표면", Roi = VisionRoi.Rect(238, 116, 36, 20), Model = "models/defect-x", Threshold = 1 });
            recipe.Pack(file);
        }

        using (var zip = ZipFile.OpenRead(file))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains($"{modelPath}/bank.bin", names);
            Assert.Contains($"{modelPath}/features.onnx", names);
            Assert.Contains("models/defect-x/model.onnx", names);
            Assert.DoesNotContain(names, n => n.EndsWith("train.json"));
        }

        using var loaded = VisionRecipe.Load(file);
        using var img = loaded.LoadBaseImage();
        using var r = await loaded.CreateJob().RunAsync(img);
        Assert.NotEqual(StepState.Error, r.Steps[VisionRecipe.GoodModelStepName].State);
        Assert.Equal(StepState.Ok, r.Steps["표면"].State);
    }

    [Fact]
    public void A_changed_package_is_refused()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "plate.dvpkg");
        using (var recipe = VisionRecipe.Load(Plate))
        {
            recipe.Name = "변조 시험 " + Guid.NewGuid().ToString("N")[..6];   // 다른 패키지 해시 (캐시를 피한다)
            recipe.Pack(file);
        }
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            var e = zip.GetEntry(VisionRecipe.FileName)!;
            string text;
            using (var r = new StreamReader(e.Open()))
                text = r.ReadToEnd();
            e.Delete();
            using var w = new StreamWriter(zip.CreateEntry(VisionRecipe.FileName).Open());
            w.Write(text.Replace("\"Max\": 200", "\"Max\": 9999"));   // 문턱을 몰래 바꿈
        }

        var ex = Assert.Throws<InvalidDataException>(() => VisionRecipe.Load(file));

        Assert.Contains("손상되었거나 바뀌었습니다", ex.Message);
        Assert.Contains(VisionRecipe.FileName, ex.Message);
    }

    [Fact]
    public void A_broken_or_missing_package_says_so()
    {
        using var dir = new TempDir();
        var junk = Path.Combine(dir.Path, "junk.dvpkg");
        File.WriteAllText(junk, "not a zip");

        Assert.Contains("패키지(zip)를 읽을 수 없습니다", Assert.Throws<InvalidDataException>(() => VisionRecipe.Load(junk)).Message);
        Assert.Contains("패키지가 없습니다", Assert.Throws<FileNotFoundException>(() => VisionRecipe.Load(Path.Combine(dir.Path, "none.dvpkg"))).Message);

        var noManifest = Path.Combine(dir.Path, "plain.dvpkg");
        using (var zip = ZipFile.Open(noManifest, ZipArchiveMode.Create))
            zip.CreateEntry("studio.json");
        Assert.Contains("manifest.json 이 없습니다", Assert.Throws<InvalidDataException>(() => VisionRecipe.Load(noManifest)).Message);
    }

    [Fact]
    public async Task Recipes_that_cannot_run_are_not_packed()
    {
        using var dir = new TempDir();
        using var recipe = CopyPlate(dir);
        var file = Path.Combine(dir.Path, "x.dvpkg");

        recipe.Checks.Add(new OnnxCheck { Name = "표면", Roi = VisionRoi.Rect(238, 116, 36, 20) });
        Assert.Contains("불량 모델이 없습니다", Assert.Throws<InvalidOperationException>(() => recipe.Pack(file)).Message);
        recipe.Checks.RemoveAt(recipe.Checks.Count - 1);

        await recipe.TrainGoodModelAsync(new GoodModelTraining
        {
            Images = Directory.GetFiles(PlateInspection.SampleDir, "*_good.bmp"), Features = Tiny, InputSize = 64, Representatives = 8,
        });
        ((BlobAlignCheck)recipe.Checks[0]).Blob.MinArea = 15000;
        Assert.Contains("다시 학습", Assert.Throws<InvalidOperationException>(() => recipe.Pack(file)).Message);

        Assert.Throws<ArgumentException>(() => recipe.Pack(Path.Combine(dir.Path, "x.zip")));
        Assert.False(File.Exists(file));
    }
}
