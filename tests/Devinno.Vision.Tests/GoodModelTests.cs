using Devinno.Vision;
using Devinno.Vision.Recipes;
using OpenCvSharp;

// 양품 모델 기준 — 학습 · 저장 · 열기 · 실행 · 낡음 · 해제. 특징 모델은 테스트용 작은 모델(64×64, 무작위 가중치)이라 판정 성능은 보지 않는다
public class GoodModelTests
{
    private static string Tiny => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_features.onnx");

    private static string[] Goods => Directory.GetFiles(PlateInspection.SampleDir, "*_good.bmp").Order().ToArray();

    // Plate 레시피 폴더를 임시 폴더로 복사
    private static VisionRecipe CopyPlate(TempDir dir)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Recipes", "Plate");
        File.Copy(Path.Combine(src, VisionRecipe.FileName), Path.Combine(dir.Path, VisionRecipe.FileName));
        Directory.CreateDirectory(Path.Combine(dir.Path, "images"));
        File.Copy(Path.Combine(src, "images", "base.bmp"), Path.Combine(dir.Path, "images", "base.bmp"));
        return VisionRecipe.Load(dir.Path);
    }

    private static GoodModelTraining Training(IReadOnlyList<string>? images = null) => new()
    {
        Images = images ?? Goods,
        Features = Tiny,
        Roi = VisionRoi.Rect(100, 150, 470, 250),
        InputSize = 64,
        Representatives = 8,
    };

    [Fact]
    public async Task Training_sets_the_model_and_the_job_runs_it_after_alignment()
    {
        using var dir = new TempDir();
        using var recipe = CopyPlate(dir);
        var stages = new List<string>();

        var t = await recipe.TrainGoodModelAsync(Training(), new SyncProgress<GoodModelProgress>(p => stages.Add(p.Stage)));

        Assert.Equal(9, Goods.Length);
        Assert.Equal(8, t.Learned);
        var score = Assert.Single(t.CalibrationScores);
        Assert.Equal(Math.Round(score * 1.05, 3), t.Model.Max);
        Assert.Empty(t.Skipped);
        Assert.True(t.MsPerImage > 0);
        Assert.Equal(["crop", "learn", "limit"], stages.Distinct());
        Assert.Same(t.Model, recipe.GoodModel);
        Assert.StartsWith("models/good-", t.Model.Path);
        foreach (var f in new[] { "model.json", "bank.bin", "features.onnx" })
            Assert.True(File.Exists(Path.Combine(dir.Path, t.Model.Path, f)), f);
        Assert.True(recipe.GoodModelIsCurrent);

        var job = recipe.CreateJob();
        using var img = recipe.LoadBaseImage();
        using var r = await job.RunAsync(img);

        Assert.Equal(["정렬", VisionRecipe.GoodModelStepName, "왼쪽 구멍", "오른쪽 구멍", "노치", "길이"], r.Steps.Select(s => s.Name));
        var step = r.Steps[VisionRecipe.GoodModelStepName];
        Assert.Equal(VisionRecipe.PoseName, step.Followed);
        Assert.NotEqual(StepState.Error, step.State);
        var v = Assert.Single(r.Values, v => v.Step == VisionRecipe.GoodModelStepName);
        Assert.Equal("양품 모델 점수", v.Name);
        Assert.Equal(t.Model.Max, v.Max);
    }

    [Fact]
    public async Task Saved_model_reopens_and_the_file_becomes_version_2()
    {
        using var dir = new TempDir();
        string path;
        using (var recipe = CopyPlate(dir))
        {
            path = (await recipe.TrainGoodModelAsync(Training())).Model.Path;
            recipe.Save();
        }

        var text = File.ReadAllText(Path.Combine(dir.Path, VisionRecipe.FileName));
        Assert.Contains("\"FormatVersion\": 2", text);
        Assert.Contains("\"GoodModel\"", text);

        using var again = VisionRecipe.Load(dir.Path);
        Assert.Equal(path, again.GoodModel!.Path);
        Assert.True(again.GoodModelIsCurrent);
        using var img = again.LoadBaseImage();
        using var r = await again.CreateJob().RunAsync(img);
        Assert.NotEqual(StepState.Error, r.Steps[VisionRecipe.GoodModelStepName].State);
    }

    [Fact]
    public async Task Changing_the_alignment_after_training_asks_to_train_again()
    {
        using var dir = new TempDir();
        using var recipe = CopyPlate(dir);
        await recipe.TrainGoodModelAsync(Training());

        var align = (BlobAlignCheck)recipe.Checks[0];
        align.Reference = align.Reference!.Value with { X = align.Reference.Value.X + 5 };

        Assert.False(recipe.GoodModelIsCurrent);
        var ex = Assert.Throws<InvalidOperationException>(() => recipe.CreateJob());
        Assert.Contains("다시 학습", ex.Message);
    }

    [Fact]
    public async Task Missing_model_folder_is_reported_with_its_path()
    {
        using var dir = new TempDir();
        string path;
        using (var recipe = CopyPlate(dir))
        {
            path = (await recipe.TrainGoodModelAsync(Training())).Model.Path;
            recipe.Save();
        }
        Directory.Delete(Path.Combine(dir.Path, path), recursive: true);

        var ex = Assert.Throws<FileNotFoundException>(() => VisionRecipe.Load(dir.Path));
        Assert.Contains("양품 모델이 없습니다", ex.Message);
        Assert.Contains(path.Replace('/', Path.DirectorySeparatorChar), ex.Message);

        using var studio = VisionRecipe.Load(dir.Path, loadModels: false);   // Studio 는 열 수 있다
        Assert.Throws<FileNotFoundException>(() => studio.CreateJob());
    }

    [Fact]
    public async Task Jobs_stop_working_after_the_recipe_is_disposed()
    {
        using var dir = new TempDir();
        var recipe = CopyPlate(dir);
        await recipe.TrainGoodModelAsync(Training());
        var job = recipe.CreateJob();
        using var img = recipe.LoadBaseImage();

        recipe.Dispose();
        using var r = await job.RunAsync(img);

        Assert.Equal(VisionState.Error, r.State);
        Assert.Contains("양품 모델이 닫혔습니다", r.Steps[VisionRecipe.GoodModelStepName].Reason);
        Assert.Throws<ObjectDisposedException>(() => recipe.CreateJob());
    }

    [Fact]
    public async Task Save_removes_model_folders_the_recipe_no_longer_uses()
    {
        using var dir = new TempDir();
        using var recipe = CopyPlate(dir);
        var first = (await recipe.TrainGoodModelAsync(Training())).Model.Path;
        var second = (await recipe.TrainGoodModelAsync(Training())).Model.Path;

        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, first)));   // 저장 전에는 옛 모델이 남는다 (변경을 버릴 수 있게)
        recipe.Save();

        Assert.False(Directory.Exists(Path.Combine(dir.Path, first)));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, second)));
    }

    [Fact]
    public async Task Images_where_the_part_is_not_found_are_left_out_with_the_reason()
    {
        using var dir = new TempDir();
        using var recipe = CopyPlate(dir);
        var black = Path.Combine(dir.Path, "black.bmp");
        using (var m = new Mat(480, 640, MatType.CV_8UC3, Scalar.Black))
            m.SaveImage(black);

        var t = await recipe.TrainGoodModelAsync(Training([.. Goods, black]));

        var s = Assert.Single(t.Skipped);
        Assert.Equal(black, s.File);
        Assert.Contains("부품 없음", s.Reason);
    }

    [Fact]
    public async Task Bad_training_input_fails_clearly_and_leaves_the_recipe_unchanged()
    {
        using var dir = new TempDir();
        using var recipe = CopyPlate(dir);

        var few = await Assert.ThrowsAsync<ArgumentException>(() => recipe.TrainGoodModelAsync(Training(Goods.Take(2).ToArray())));
        Assert.Contains("최소 3장", few.Message);

        var noFeatures = await Assert.ThrowsAsync<FileNotFoundException>(() => recipe.TrainGoodModelAsync(new GoodModelTraining { Images = Goods, Features = Path.Combine(dir.Path, "none.onnx") }));
        Assert.Contains("export_features.py", noFeatures.Message);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recipe.TrainGoodModelAsync(Training(), ct: cts.Token));

        Assert.Null(recipe.GoodModel);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "models")));
    }

    [Fact]
    public void A_check_cannot_take_the_model_step_name()
    {
        var recipe = VisionRecipe.Create(Path.GetTempPath(), "t");
        recipe.GoodModel = new GoodModel { Path = "models/good-x", Roi = VisionRoi.Rect(0, 0, 10, 10), Max = 1 };
        recipe.Checks.Add(new BrightnessCheck { Name = VisionRecipe.GoodModelStepName });

        Assert.Contains("양품 모델 단계가 씁니다", Assert.Throws<InvalidOperationException>(() => recipe.CreateJob()).Message);
    }

    // Progress<T> 는 동기화 문맥으로 넘겨 순서가 섞일 수 있다 — 테스트는 바로 부른다
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
