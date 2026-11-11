using System.Text.Json.Nodes;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;
using OpenCvSharp;

namespace Devinno.Vision.Studio.Tests;

public class TestRunnerTests
{
    // 밝기 80 정사각형 — 평균 밝기 검사(50~255)로 OK, 어두운 것(10)은 NG
    private static void WritePng(string path, byte gray)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var m = new Mat(32, 32, MatType.CV_8UC1, new Scalar(gray));
        Cv2.ImWrite(path, m);
    }

    // 평균 밝기 50 이상이면 OK 인 로직
    private static StudioSession BrightLogic(TempDir dir, string baseImage)
    {
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("project"), "밝기").Ok);
        Assert.True(s.SetBaseImage(baseImage).Ok);
        var id = s.AddCheck("Brightness", "전체").Id!;
        s.SetRoi(id, VisionRoi.Rect(0, 0, 32, 32));
        Assert.True(s.UpdateCheck(id, JsonNode.Parse("""{"Min":50,"Max":255}""")!.AsObject()).Ok);
        return s;
    }

    [Fact]
    public async Task FileName_rule_reads_labels()
    {
        using var dir = new TempDir();
        var images = dir.Sub("images");
        WritePng(Path.Combine(images, "01_good.png"), 200);
        WritePng(Path.Combine(images, "02_dark.png"), 10);
        WritePng(Path.Combine(images, "03_good.png"), 10);   // 양품인데 NG → 틀림
        var s = BrightLogic(dir, Path.Combine(images, "01_good.png"));
        Assert.True(s.AddTestSet(images, LabelRule.FileName).Ok);

        var r = await s.RunTestAsync(0);

        Assert.True(r.Ok, r.Message);
        Assert.Equal((2, 3), (r.Correct, r.Total));
        Assert.Equal(["good", "dark", "good"], r.Items.Select(i => i.Label));
        Assert.Equal([true, false, true], r.Items.Select(i => i.ExpectOk));
        var good = r.Labels.Single(l => l.Label == "good");
        Assert.Equal((true, 2, 1), (good.ExpectOk, good.Count, good.Correct));
        var dark = r.Labels.Single(l => l.Label == "dark");
        Assert.Equal((false, 1, 1), (dark.ExpectOk, dark.Count, dark.Correct));
        Assert.Same(r, s.LastReport);
    }

    [Fact]
    public async Task Folder_rule_reads_labels()
    {
        using var dir = new TempDir();
        var images = dir.Sub("test");
        WritePng(Path.Combine(images, "good", "000.png"), 200);
        WritePng(Path.Combine(images, "good", "001.png"), 200);
        WritePng(Path.Combine(images, "crack", "000.png"), 10);
        var s = BrightLogic(dir, Path.Combine(images, "good", "000.png"));
        s.AddTestSet(images, LabelRule.Folder);

        var r = await s.RunTestAsync(0);

        Assert.Equal((3, 3), (r.Correct, r.Total));
        Assert.Equal(["crack", "good"], r.Labels.Select(l => l.Label).Order());
        Assert.Equal(2, r.Labels.Single(l => l.Label == "good").Count);
    }

    [Fact]
    public async Task Non_images_are_skipped_and_broken_image_is_an_error_item()
    {
        using var dir = new TempDir();
        var images = dir.Sub("images");
        WritePng(Path.Combine(images, "01_good.png"), 200);
        File.WriteAllText(Path.Combine(images, "02_good.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(images, "03_good.png"), [1, 2, 3, 4, 5]);
        var s = BrightLogic(dir, Path.Combine(images, "01_good.png"));
        s.AddTestSet(images, LabelRule.FileName);

        var r = await s.RunTestAsync(0);

        Assert.Equal(2, r.Total);
        var broken = r.Items.Single(i => i.File.EndsWith("03_good.png"));
        Assert.Equal("Error", broken.State);
        Assert.False(string.IsNullOrEmpty(broken.Error));
        Assert.False(broken.Correct);
        Assert.Equal(1, r.Correct);
    }

    [Fact]
    public async Task Empty_folder_says_no_images()
    {
        using var dir = new TempDir();
        var empty = dir.Sub("empty");
        var images = dir.Sub("images");
        WritePng(Path.Combine(images, "01_good.png"), 200);
        var s = BrightLogic(dir, Path.Combine(images, "01_good.png"));
        s.AddTestSet(empty, LabelRule.FileName);

        var r = await s.RunTestAsync(0);

        Assert.False(r.Ok);
        Assert.Equal("이미지 없음", r.Message);
        Assert.Equal(0, r.Total);
    }

    [Fact]
    public async Task Test_without_base_image_or_set_says_so()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        s.AddCheck("Brightness", "b");

        var noBase = await s.RunTestAsync(0);
        Assert.False(noBase.Ok);
        Assert.Contains("기준 이미지", noBase.Message);

        s.SetBaseImage(Samples.Bracket("01_good.bmp"));
        var noSet = await s.RunTestAsync(0);
        Assert.False(noSet.Ok);
        Assert.Contains("테스트 폴더", noSet.Message);
    }

    [Fact]
    public async Task Roi_outside_after_alignment_is_reported_not_thrown()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        s.SetBaseImage(Samples.Bracket("01_good.bmp"));
        var align = s.AddCheck("BlobAlign", "정렬").Id!;
        s.UpdateCheck(align, JsonNode.Parse("""{"Blob":{"MinArea":20000}}""")!.AsObject());
        Assert.True(s.TeachBase().Ok);
        var corner = s.AddCheck("PixelCount", "모서리").Id!;
        s.SetRoi(corner, VisionRoi.Rect(600, 450, 100, 100));
        s.AddTestSet(Samples.BracketDir, LabelRule.FileName);

        var r = await s.RunTestAsync(0);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(18, r.Total);
        Assert.Equal(0, r.Correct);   // Error 는 기대가 NG 여도 정답이 아니다 → 틀린 목록에 사유와 함께
        Assert.All(r.Items, i => Assert.False(i.Correct));
        Assert.All(r.Items, i =>
        {
            Assert.Equal("Error", i.State);
            Assert.Contains("outside", i.Error);
        });
    }

    [Fact]
    public async Task Files_without_underscore_have_no_label_and_are_not_judged()
    {
        using var dir = new TempDir();
        var images = dir.Sub("images");
        WritePng(Path.Combine(images, "01_good.png"), 200);
        WritePng(Path.Combine(images, "scan0007.png"), 10);   // '_' 없음 → 라벨 없음 (NG 로 추측하지 않는다)
        var s = BrightLogic(dir, Path.Combine(images, "01_good.png"));
        s.AddTestSet(images, LabelRule.FileName);

        var before = s.TestImages(0);
        var r = await s.RunTestAsync(0);

        Assert.Equal([("01_good.png", "good"), ("scan0007.png", (string?)null)], before.Select(i => (Path.GetFileName(i.File), i.Label)));
        Assert.Equal((1, 1, 1), (r.Correct, r.Total, r.Unlabeled));
        var none = r.Items.Single(i => i.File.EndsWith("scan0007.png"));
        Assert.Equal(("Ng", (bool?)null, (bool?)null), (none.State, none.ExpectOk, none.Correct));
        Assert.DoesNotContain(r.Labels, l => l.Label == "scan0007");
        Assert.Contains("라벨 없음 1", r.Message);
    }

    [Fact]
    public void Project_folder_itself_cannot_be_a_test_folder_but_a_subfolder_can()
    {
        using var dir = new TempDir();
        var images = dir.Sub("images");
        WritePng(Path.Combine(images, "01_good.png"), 200);
        var s = BrightLogic(dir, Path.Combine(images, "01_good.png"));
        var project = s.Project!.Folder;

        var self = s.AddTestSet(project, LabelRule.FileName);
        var inside = s.AddTestSet(Path.Combine(project, "images"), LabelRule.FileName);
        var above = s.AddTestSet(dir.Path, LabelRule.Folder);
        var sibling = s.AddTestSet(images, LabelRule.FileName);

        Assert.False(self.Ok);
        Assert.Contains("프로젝트 폴더", self.Message);
        Assert.True(inside.Ok, inside.Message);   // 프로젝트 안 images/ (기준 이미지 사본이 있는 곳) — 사본은 목록에서 뺀다
        Assert.False(above.Ok);
        Assert.True(sibling.Ok, sibling.Message);
        Assert.Equal(2, s.TestSets.Count);
        Assert.DoesNotContain(s.TestImages(0), i => string.Equals(i.File, s.BaseImageFile, StringComparison.OrdinalIgnoreCase));
        Assert.False(s.AddTestSet(Path.Combine(project, "models"), LabelRule.FileName).Ok);
    }
}
