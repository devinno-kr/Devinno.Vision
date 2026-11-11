using Devinno.Vision;
using Devinno.Vision.Recipes;
using OpenCvSharp;

// 불량 모델 · ONNX 검사. 모델은 테스트용 작은 분류 모델(32×32, 무작위 가중치, 라벨 3개)이라 분류 성능은 보지 않는다
public class DefectModelTests
{
    private static string Tiny => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_classifier.onnx");

    private const string Info = """{ "Labels": ["good", "scratch", "dent"], "GoodLabel": "good", "InputWidth": 32, "InputHeight": 32, "Normalize": "ImageNet" }""";

    // 모델 폴더 (model.onnx + model.json)
    internal static string ModelFolder(string root, string rel, string info = Info)
    {
        var dir = Path.Combine(root, rel);
        Directory.CreateDirectory(dir);
        File.Copy(Tiny, Path.Combine(dir, DefectModel.ModelFile));
        File.WriteAllText(Path.Combine(dir, DefectModel.InfoFile), info);
        return dir;
    }

    private static VisionRecipe CopyBracket(TempDir dir)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Recipes", "Bracket");
        File.Copy(Path.Combine(src, VisionRecipe.FileName), Path.Combine(dir.Path, VisionRecipe.FileName));
        return VisionRecipe.Load(dir.Path);
    }

    [Fact]
    public void Classify_gives_a_probability_per_label()
    {
        using var dir = new TempDir();
        using var model = DefectModel.Load(ModelFolder(dir.Path, "m"));
        using var img = VisionImage.Load(BracketInspection.Sample("01_good.bmp"));

        var r = model.Classify(img);

        Assert.Equal(["good", "scratch", "dent"], model.Labels);
        Assert.Equal((32, 32), (model.InputWidth, model.InputHeight));
        Assert.Equal(1, r.Probabilities.Values.Sum(), 6);
        Assert.Equal(r.Probabilities.MaxBy(kv => kv.Value).Key, r.Label);
        Assert.NotEqual("good", r.Defect);
        Assert.Equal(1 - r.Probabilities["good"], r.DefectProbability, 9);
    }

    [Theory]
    [InlineData("""{ "Labels": ["good"], "InputWidth": 32, "InputHeight": 32 }""", "2개 이상")]
    [InlineData("""{ "Labels": ["ok", "scratch"], "InputWidth": 32, "InputHeight": 32 }""", "양품 라벨 'good'")]
    [InlineData("""{ "Labels": ["good", "scratch", "dent"], "InputWidth": 0, "InputHeight": 32 }""", "InputWidth")]
    [InlineData("""{ "Labels": ["good", "scratch", "dent"], "InputWidth": 32, "InputHeight": 32, "Normalize": "Lab" }""", "Normalize")]
    [InlineData("""{ "Labels": ["good", "scratch" """, "읽을 수 없습니다")]
    public void Wrong_model_json_is_reported(string info, string expected)
    {
        using var dir = new TempDir();
        var folder = ModelFolder(dir.Path, "m", info);

        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => DefectModel.Load(folder)).Message);
    }

    [Fact]
    public void Output_count_must_match_the_labels()
    {
        using var dir = new TempDir();
        using var model = DefectModel.Load(ModelFolder(dir.Path, "m", """{ "Labels": ["good", "scratch"], "InputWidth": 32, "InputHeight": 32 }"""));
        using var img = VisionImage.Load(BracketInspection.Sample("01_good.bmp"));

        Assert.Contains("라벨은 2개", Assert.Throws<InvalidDataException>(() => model.Classify(img)).Message);
    }

    [Fact]
    public async Task Onnx_check_judges_by_the_defect_probability_and_names_the_defect()
    {
        using var dir = new TempDir();
        using var recipe = CopyBracket(dir);
        ModelFolder(dir.Path, "models/defect-a");
        var check = new OnnxCheck { Name = "표면", Roi = VisionRoi.Rect(268, 192, 134, 36), Model = "models/defect-a", Threshold = 0 };
        recipe.Checks.Add(check);
        using var img = VisionImage.Load(BracketInspection.Sample("01_good.bmp"));

        using (var ng = await recipe.CreateJob().RunAsync(img))
        {
            Assert.Equal(VisionState.Ng, ng.State);   // 문턱 0 → 늘 NG
            var code = Assert.Single(ng.Codes);
            Assert.Contains(code, new[] { "SCRATCH", "DENT" });
            var v = Assert.Single(ng.Values, v => v.Step == "표면");
            Assert.Equal("표면 불량 확률", v.Name);
            Assert.Equal(VisionRecipe.PoseName, ng.Steps["표면"].Followed);
        }

        check.Threshold = 1;
        using var ok = await recipe.CreateJob().RunAsync(img);
        Assert.Equal(StepState.Ok, ok.Steps["표면"].State);
    }

    [Fact]
    public void Onnx_check_without_a_model_cannot_run_but_the_recipe_still_opens()
    {
        using var dir = new TempDir();
        using (var recipe = CopyBracket(dir))
        {
            recipe.Checks.Add(new OnnxCheck { Name = "표면", Roi = VisionRoi.Rect(268, 192, 134, 36) });
            recipe.Save();
        }

        Assert.Contains("\"FormatVersion\": 2", File.ReadAllText(Path.Combine(dir.Path, VisionRecipe.FileName)));
        using var again = VisionRecipe.Load(dir.Path);   // 학습 전 프로젝트도 열린다
        var ex = Assert.Throws<InvalidOperationException>(() => again.CreateJob());
        Assert.Contains("불량 모델이 없습니다", ex.Message);
    }

    [Fact]
    public void Missing_defect_model_folder_is_reported_when_loading()
    {
        using var dir = new TempDir();
        using (var recipe = CopyBracket(dir))
        {
            recipe.Checks.Add(new OnnxCheck { Name = "표면", Roi = VisionRoi.Rect(268, 192, 134, 36), Model = "models/defect-gone" });
            recipe.Save();
        }

        Assert.Contains("불량 모델이 없습니다", Assert.Throws<FileNotFoundException>(() => VisionRecipe.Load(dir.Path)).Message);
        using var studio = VisionRecipe.Load(dir.Path, loadModels: false);
        Assert.Throws<FileNotFoundException>(() => studio.CreateJob());
    }

    [Fact]
    public void Save_removes_defect_model_folders_no_check_uses()
    {
        using var dir = new TempDir();
        using var recipe = CopyBracket(dir);
        var used = ModelFolder(dir.Path, "models/defect-used");
        var old = ModelFolder(dir.Path, "models/defect-old");
        var other = Directory.CreateDirectory(Path.Combine(dir.Path, "models", "mine")).FullName;   // 사용자가 둔 폴더는 건드리지 않는다
        recipe.Checks.Add(new OnnxCheck { Name = "표면", Roi = VisionRoi.Rect(268, 192, 134, 36), Model = "models/defect-used" });

        recipe.Save();

        Assert.True(Directory.Exists(used));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task Crop_cuts_the_region_where_the_part_is()
    {
        using var dir = new TempDir();
        using var recipe = CopyBracket(dir);
        using var img = VisionImage.Load(BracketInspection.Sample("18_good.bmp"));   // 회전된 부품

        using var crop = await recipe.CropAsync(img, VisionRoi.Rect(268, 192, 134, 36));

        Assert.Equal((134, 36), (crop.Width, crop.Height));
        using var black = VisionImage.From(new Mat(480, 640, MatType.CV_8UC3, Scalar.Black));
        Assert.Contains("부품 없음", (await Assert.ThrowsAsync<InvalidOperationException>(() => recipe.CropAsync(black, VisionRoi.Rect(0, 0, 10, 10)))).Message);
    }

    [Fact]
    public void Onnx_threshold_must_be_a_probability()
    {
        var c = new OnnxCheck { Name = "표면", Threshold = 1.5 };

        Assert.Contains("Threshold", c.Validate());
        Assert.Contains("Onnx", CheckDef.Kinds);
        Assert.IsType<OnnxCheck>(CheckDef.New("Onnx"));
    }
}
