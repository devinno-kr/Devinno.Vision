using System.Text.Json.Nodes;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Tests;

public class SessionTests
{
    private static string Add(StudioSession s, string kind, string name)
    {
        var r = s.AddCheck(kind, name);
        Assert.True(r.Ok, r.Message);
        return r.Id!;
    }

    [Fact]
    public void Edit_commands_change_the_project_and_raise_changed()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Path, "플레이트").Ok);
        var changed = 0;
        s.ProjectChanged += () => changed++;

        var a = Add(s, "BlobCount", "왼쪽");
        var b = Add(s, "Brightness", "밝기");
        Assert.True(s.MoveCheck(b, 0).Ok);
        Assert.True(s.SetRoi(a, VisionRoi.Rect(1, 2, 30, 40)).Ok);
        var u = s.UpdateCheck(a, JsonNode.Parse("""{"Min":2,"Name":"구멍"}""")!.AsObject());
        Assert.True(u.Ok, u.Message);

        Assert.Equal(["밝기", "구멍"], s.Project!.Checks.Select(c => c.Name));
        var hole = Assert.IsType<BlobCountCheck>(s.GetCheck(a));
        Assert.Equal(2, hole.Min);
        Assert.Equal(1, hole.Max);                       // 안 준 속성은 그대로
        Assert.Equal(a, hole.Id);
        Assert.Equal(VisionRoi.Rect(1, 2, 30, 40), hole.Roi);
        Assert.Equal(5, changed);
    }

    [Fact]
    public void Update_rejects_unknown_property_and_keeps_kind_and_id()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        var a = Add(s, "BlobCount", "a");

        var bad = s.UpdateCheck(a, JsonNode.Parse("""{"Hologram":1}""")!.AsObject());
        var kind = s.UpdateCheck(a, JsonNode.Parse("""{"Kind":"Length","Id":"zz","max":3,"Blob":{"MinArea":20000}}""")!.AsObject());

        Assert.False(bad.Ok);
        Assert.Contains("Hologram", bad.Message);
        Assert.True(kind.Ok, kind.Message);
        var c = Assert.IsType<BlobCountCheck>(s.GetCheck(a));
        Assert.Equal(3, c.Max);
        Assert.Equal(20000, c.Blob.MinArea);
        Assert.Equal(new BlobParams().Threshold, c.Blob.Threshold);   // 중첩 객체도 일부만 바뀐다
    }

    [Fact]
    public void Edits_without_project_say_so()
    {
        var s = new StudioSession();

        var r = s.AddCheck("BlobCount", "a");

        Assert.False(r.Ok);
        Assert.Equal("프로젝트를 먼저 만들거나 여세요", r.Message);
    }

    [Fact]
    public void Duplicate_name_is_rejected()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        Add(s, "BlobCount", "구멍");

        var r = s.AddCheck("PixelCount", "구멍");

        Assert.False(r.Ok);
        Assert.Equal("같은 이름의 검사가 있습니다: 구멍", r.Message);
        Assert.Single(s.Project!.Checks);
    }

    [Fact]
    public void Second_align_is_rejected()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        Add(s, "BlobAlign", "정렬");

        var r = s.AddCheck("BlobAlign", "정렬2");

        Assert.False(r.Ok);
        Assert.Single(s.Project!.Checks);
    }

    [Fact]
    public void Align_goes_first()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        var a = Add(s, "BlobCount", "a");
        Add(s, "BlobAlign", "정렬");

        Assert.Equal(["정렬", "a"], s.Project!.Checks.Select(c => c.Name));
        Assert.False(s.MoveCheck(a, 0).Ok);   // 정렬 앞으로는 못 간다
    }

    [Fact]
    public void Teach_without_base_image_says_so()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        Add(s, "BlobAlign", "정렬");

        var r = s.TeachBase();

        Assert.False(r.Ok);
        Assert.Contains("기준 이미지", r.Message);
    }

    [Fact]
    public void Teach_stores_reference_pose()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        Assert.True(s.SetBaseImage(Samples.Plate("01_good.bmp")).Ok);
        var a = Add(s, "BlobAlign", "정렬");
        s.UpdateCheck(a, JsonNode.Parse("""{"Blob":{"MinArea":20000}}""")!.AsObject());

        var r = s.TeachBase();

        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(dir.Path, s.Project!.BaseImage!)));
        var pose = Assert.IsType<BlobAlignCheck>(s.GetCheck(a)).Reference!.Value;
        Assert.Equal(319.1, pose.X, 0.5);
        Assert.Equal(240.7, pose.Y, 0.5);
        Assert.Equal(-2.3, pose.Angle, 0.5);
    }

    [Fact]
    public void Concurrent_commands_are_serialized()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");

        Parallel.For(0, 50, i => s.AddCheck("BlobCount", $"c{i}"));

        Assert.Equal(50, s.Project!.Checks.Count);
        Assert.Equal(50, s.Project.Checks.Select(c => c.Name).Distinct().Count());
        Assert.Equal(50, s.Project.Checks.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Save_and_open_round_trip()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        Add(s, "BlobCount", "a");
        Assert.True(s.SaveProject().Ok);

        var t = new StudioSession();
        var r = t.OpenProject(dir.Path);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("a", Assert.Single(t.Project!.Checks).Name);
        Assert.False(t.OpenProject(Path.Combine(dir.Path, "none")).Ok);
        Assert.False(s.NewProject(dir.Path, "again").Ok);   // 있는 프로젝트를 덮지 않는다
    }

    [Fact]
    public async Task Run_image_reports_values()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        var a = Add(s, "BlobCount", "왼쪽 구멍");
        s.SetRoi(a, VisionRoi.Rect(138, 130, 60, 60));
        s.UpdateCheck(a, JsonNode.Parse("""{"Blob":{"Mode":"Fixed","Threshold":80,"Invert":true,"MinArea":300},"Min":1,"Max":1,"Code":"HOLE"}""")!.AsObject());

        var ok = await s.RunImageAsync(Samples.Plate("01_good.bmp"));
        var ng = await s.RunImageAsync(Samples.Plate("02_missinghole.bmp"));
        var missing = await s.RunImageAsync(Path.Combine(dir.Path, "nope.png"));

        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("Ok", ok.State);
        var row = Assert.Single(ok.Values);
        Assert.Equal(("왼쪽 구멍", 1.0, true), (row.Check, row.Value, row.Ok));
        Assert.Equal("Ng", ng.State);
        Assert.Equal(["HOLE"], ng.Codes);
        Assert.False(missing.Ok);
    }

    [Fact]
    public async Task Run_image_returns_png_with_judgment()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        PlateStudioTests.BuildPlate(s, dir.Path);

        var r = await s.RunImageAsync(Samples.Plate("02_missinghole.bmp"));

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Ng", r.State);
        Assert.Equal(["HOLE"], r.Codes);
        Assert.NotNull(r.Png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, r.Png![..4]);
        Assert.True(r.Png.Length > 20 * 1024, $"{r.Png.Length} bytes");
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "run-02.png"), r.Png);   // 눈으로 확인용
    }

    [Fact]
    public async Task Render_test_item_returns_png()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        PlateStudioTests.BuildPlate(s, dir.Path);
        Assert.Null(await s.RenderTestItemAsync(0));   // 보고서 전

        var report = await s.RunTestAsync(0);
        var png = await s.RenderTestItemAsync(1);

        Assert.NotNull(png);
        Assert.Equal(0x89, png![0]);
        Assert.Null(await s.RenderTestItemAsync(report.Total));   // 범위 밖
    }

    // I-2: 읽히지만 잘못된 studio.json — 지금 프로젝트를 망가뜨리지 않고 실패
    [Theory]
    [InlineData("\"Checks\": [", "\"Checks\": null, \"X\": [")]
    [InlineData("\"TestSets\": [", "\"TestSets\": null, \"Y\": [")]
    [InlineData("\"Name\": \"b\"", "\"Name\": \"a\"")]
    [InlineData("\"Name\": \"b\"", "\"Name\": \"\"")]
    public void Open_bad_but_parseable_file_keeps_current_project(string find, string replace)
    {
        using var dir = new TempDir();
        var bad = new StudioSession();
        bad.NewProject(dir.Sub("bad"), "bad");
        bad.AddCheck("BlobCount", "a");
        bad.AddCheck("BlobCount", "b");
        bad.SaveProject();
        var file = Path.Combine(dir.Path, "bad", VisionRecipe.FileName);
        var text = File.ReadAllText(file);
        Assert.Contains(find, text);
        File.WriteAllText(file, text.Replace(find, replace));
        var s = new StudioSession();
        s.NewProject(dir.Sub("good"), "good");

        var r = s.OpenProject(Path.Combine(dir.Path, "bad"));

        Assert.False(r.Ok);
        Assert.Equal("good", s.Project!.Name);
    }

    [Fact]
    public void Update_rejects_null_object()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Path, "p");
        var a = Add(s, "BlobCount", "a");

        var r = s.UpdateCheck(a, JsonNode.Parse("""{"Blob":null}""")!.AsObject());

        Assert.False(r.Ok);
        Assert.NotNull(((BlobCountCheck)s.GetCheck(a)!).Blob);
    }

    // I-3: 파일 문제는 예외가 아니라 실패 결과
    [Fact]
    public void File_problems_are_results_not_exceptions()
    {
        using var dir = new TempDir();
        var blocker = Path.Combine(dir.Path, "file.txt");
        File.WriteAllText(blocker, "x");
        var s = new StudioSession();

        var newInFile = s.NewProject(Path.Combine(blocker, "sub"), "p");   // 폴더 자리에 파일
        Assert.False(newInFile.Ok);

        s.NewProject(dir.Sub("p"), "p");
        Assert.True(s.SetBaseImage(Samples.Plate("01_good.bmp")).Ok);
        s.AddCheck("BlobAlign", "정렬");
        File.WriteAllBytes(s.BaseImageFile!, [1, 2, 3]);                  // 기준 이미지가 나중에 깨짐
        var teach = s.TeachBase();
        Assert.False(teach.Ok);
        Assert.Contains("기준 이미지", teach.Message);
    }

    // 저장 안 된 변경 — 작업을 잃지 않게
    [Fact]
    public void Edits_mark_the_project_unsaved_until_saved()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.False(s.IsDirty);
        s.NewProject(dir.Path, "p");
        Assert.False(s.IsDirty);                       // 새 로직은 바로 저장된다

        s.AddCheck("BlobCount", "a");
        Assert.True(s.IsDirty);
        var changed = 0;
        s.ProjectChanged += () => changed++;
        Assert.True(s.SaveProject().Ok);
        Assert.False(s.IsDirty);
        Assert.Equal(1, changed);                      // 화면이 "저장 안 됨" 을 지우게

        s.SetCalibration(0.1);
        Assert.True(s.IsDirty);
        Assert.True(s.OpenProject(dir.Path, discardChanges: true).Ok);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Opening_or_creating_another_logic_with_unsaved_changes_is_refused_unless_discarded()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        s.NewProject(dir.Sub("a"), "a");
        s.NewProject(dir.Sub("b"), "b");
        s.AddCheck("BlobCount", "unsaved");

        var open = s.OpenProject(Path.Combine(dir.Path, "a"));
        var create = s.NewProject(dir.Sub("c"), "c");

        Assert.False(open.Ok);
        Assert.Contains("저장", open.Message);
        Assert.False(create.Ok);
        Assert.Equal("b", s.Project!.Name);            // 지금 로직과 변경은 그대로
        Assert.Single(s.Checks);

        Assert.True(s.OpenProject(Path.Combine(dir.Path, "a"), discardChanges: true).Ok);
        Assert.Equal("a", s.Project!.Name);
    }

    [Fact]
    public async Task Running_and_testing_do_not_mark_the_project_unsaved()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        PlateStudioTests.BuildPlate(s, dir.Path);
        s.SaveProject();

        await s.RunImageAsync(Samples.Plate("01_good.bmp"));
        await s.RunTestAsync(0);

        Assert.False(s.IsDirty);
    }
}
