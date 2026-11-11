using Devinno.Vision;
using Devinno.Vision.Recipes;
using Xunit.Abstractions;

// Studio 가 만든 레시피 폴더(studio.json + images/)를 라이브러리만으로 불러와 검사한다.
// Recipes/Bracket 은 Studio 예제(studio-bracket-test)의 studio.json 그대로다 (TestSets 의 폴더 경로만 바꿈).
public class RecipeTests(ITestOutputHelper output)
{
    private static string BracketRecipe => Path.Combine(AppContext.BaseDirectory, "Recipes", "Bracket");

    [Fact]
    public async Task Bracket_recipe_judges_all_18_samples_like_studio()
    {
        var recipe = VisionRecipe.Load(BracketRecipe);
        var job = recipe.CreateJob();

        var files = Directory.GetFiles(BracketInspection.SampleDir, "*.bmp").Order().ToArray();
        var failures = new List<string>();
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            using var img = VisionImage.Load(f);
            using var r = await job.RunAsync(img);
            var (state, code) = name.Split('_')[1] switch
            {
                "good" => (VisionState.Ok, (string?)null),
                "missinghole" => (VisionState.Ng, "HOLE"),
                "tabdefect" => (VisionState.Ng, "DISCOLOR"),
                "distancedefect" => (VisionState.Ng, "LENGTH"),
                var other => throw new InvalidOperationException(other),
            };
            var codes = string.Join(",", r.Codes);
            var length = r.Values.Single(v => v.Step == "길이");
            output.WriteLine($"{name,-20} {r.State,-5} {codes,-10} 길이 {length.Value:0.00} {length.Unit}");
            if (r.State != state || (code is not null && !(r.Codes.Count == 1 && r.Codes[0] == code)))
                failures.Add($"{name}: expected {state} {code}, got {r.State} {codes} {r.Error}");

            // 길이는 mm 로 판정한다 (캘리브레이션 0.218978 mm/px, 90 ±1)
            Assert.Equal("mm", length.Unit);
            Assert.Equal(90, length.Nominal);
            Assert.Equal(length.Value is >= 89 and <= 91, length.Ok);
            Assert.Equal(code == "LENGTH", !length.Ok);
        }

        Assert.Equal(18, files.Length);
        Assert.Empty(failures);
    }

    [Fact]
    public void Load_restores_calibration_base_image_and_reference_pose()
    {
        var recipe = VisionRecipe.Load(BracketRecipe);

        Assert.Equal("브래킷", recipe.Name);
        Assert.Equal(BracketRecipe, recipe.Folder);
        Assert.Equal(0.218978, recipe.Calibration!.MmPerPx);
        Assert.Equal(["BlobAlign", "BlobCount", "BlobCount", "PixelCount", "Length"], recipe.Checks.Select(c => c.Kind));

        var align = Assert.IsType<BlobAlignCheck>(recipe.Checks[0]);
        Assert.Equal(new VisionPose(330.4164563051792, 256.03058368114955, -0.07183547849026792), align.Reference);

        Assert.Equal(Path.Combine(BracketRecipe, "images", "base.bmp"), Path.GetFullPath(recipe.BaseImagePath!));
        using var baseImage = recipe.LoadBaseImage();
        Assert.Equal((640, 480), (baseImage.Width, baseImage.Height));

        Assert.Equal(0.218978, recipe.CreateJob().Calibration!.MmPerPx);
    }

    [Fact]
    public async Task Base_image_runs_ok_at_the_taught_pose()
    {
        var recipe = VisionRecipe.Load(BracketRecipe);
        var reference = ((BlobAlignCheck)recipe.Checks[0]).Reference!.Value;
        using var img = recipe.LoadBaseImage();

        using var r = await recipe.CreateJob().RunAsync(img);

        Assert.Equal(VisionState.Ok, r.State);
        var pose = r.Poses[VisionRecipe.PoseName];
        Assert.Equal(reference.X, pose.X, 0.01);
        Assert.Equal(reference.Y, pose.Y, 0.01);
        Assert.Equal(reference.Angle, pose.Angle, 0.01);
        Assert.Equal(VisionRecipe.PoseName, r.Steps["길이"].Followed);
    }

    [Fact]
    public void Job_keeps_the_values_it_was_made_with()
    {
        var recipe = VisionRecipe.Load(BracketRecipe);
        var job = recipe.CreateJob();

        recipe.Calibration = null;
        recipe.Checks.Clear();

        Assert.Equal(0.218978, job.Calibration!.MmPerPx);
    }

    // ── 실패는 알기 쉬운 오류로 ─────────────────────────────

    [Fact]
    public void Folder_without_studio_json_says_so()
    {
        using var dir = new TempDir();

        var ex = Assert.Throws<FileNotFoundException>(() => VisionRecipe.Load(dir.Path));

        Assert.Contains("studio.json 이 없습니다", ex.Message);
        Assert.Contains(dir.Path, ex.Message);
    }

    [Theory]
    [InlineData("{ \"Name\": \"x\", ", "읽을 수 없습니다")]                                  // 깨진 JSON
    [InlineData("null", "비어 있습니다")]
    [InlineData("{ \"Checks\": null }", "Checks")]
    [InlineData("{ \"Checks\": [ { \"Name\": \"a\" } ] }", "Kind 가 없습니다")]
    [InlineData("{ \"Checks\": [ { \"Kind\": \"OnnxAnomaly\", \"Name\": \"a\" } ] }", "알 수 없는 검사 종류: OnnxAnomaly")]
    [InlineData("{ \"Checks\": [ { \"Kind\": \"Length\", \"Name\": \"\" } ] }", "검사 이름이 비었습니다")]
    [InlineData("{ \"Checks\": [ { \"Kind\": \"Length\", \"Name\": \"a\", \"Nominal\": \"긴\" } ] }", "읽을 수 없습니다")]
    [InlineData("{ \"Checks\": [ { \"Kind\": \"Length\", \"Id\": \"1\", \"Name\": \"a\" }, { \"Kind\": \"Length\", \"Id\": \"2\", \"Name\": \"a\" } ] }", "같은 이름의 검사")]
    [InlineData("{ \"Checks\": [ { \"Kind\": \"Length\", \"Id\": \"1\", \"Name\": \"a\" }, { \"Kind\": \"BlobAlign\", \"Id\": \"2\", \"Name\": \"b\" } ] }", "정렬 검사는 맨 앞")]
    [InlineData("{ \"FormatVersion\": 2, \"Checks\": [] }", "형식 버전(2)")]
    public void Wrong_studio_json_fails_with_a_readable_reason(string json, string expected)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, VisionRecipe.FileName), json);

        var ex = Assert.Throws<InvalidDataException>(() => VisionRecipe.Load(dir.Path));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Missing_base_image_is_reported_with_its_path_but_the_job_still_runs()
    {
        using var dir = new TempDir();
        File.Copy(Path.Combine(BracketRecipe, VisionRecipe.FileName), Path.Combine(dir.Path, VisionRecipe.FileName));

        var recipe = VisionRecipe.Load(dir.Path);   // 기준 자세는 studio.json 에 있어 실행에는 기준 이미지가 필요 없다

        Assert.Null(recipe.BaseImagePath);
        var ex = Assert.Throws<FileNotFoundException>(() => recipe.LoadBaseImage());
        Assert.Contains("기준 이미지가 없습니다", ex.Message);
        Assert.Contains(Path.Combine(dir.Path, "images", "base.bmp"), ex.Message);
        Assert.NotNull(recipe.CreateJob());
    }

    [Fact]
    public void Base_image_not_set_is_reported()
    {
        var recipe = VisionRecipe.Create(Path.GetTempPath(), "t");

        Assert.Null(recipe.BaseImagePath);
        Assert.Contains("기준 이미지가 지정되지 않았습니다", Assert.Throws<InvalidOperationException>(() => recipe.LoadBaseImage()).Message);
    }

    [Fact]
    public void Job_cannot_be_made_from_no_checks_or_broken_checks()
    {
        var recipe = VisionRecipe.Create(Path.GetTempPath(), "t");
        Assert.Contains("검사가 없습니다", Assert.Throws<InvalidOperationException>(() => recipe.CreateJob()).Message);

        recipe.Checks.Add(new BrightnessCheck { Name = "밝기" });
        recipe.Checks.Add(new BrightnessCheck { Name = "밝기" });
        Assert.Contains("같은 이름의 검사", Assert.Throws<InvalidOperationException>(() => recipe.CreateJob()).Message);
    }
}

// 테스트마다 지우는 임시 폴더
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("vision-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
