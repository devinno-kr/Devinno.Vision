using System.Text.Json.Nodes;
using Devinno.Vision.Studio.Model;
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
        Assert.True(s.NewProject(dir.Path, "브래킷").Ok);
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
        Assert.True(s.SetBaseImage(Samples.Bracket("01_good.bmp")).Ok);
        var a = Add(s, "BlobAlign", "정렬");
        s.UpdateCheck(a, JsonNode.Parse("""{"Blob":{"MinArea":20000}}""")!.AsObject());

        var r = s.TeachBase();

        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(dir.Path, s.Project!.BaseImage!)));
        var pose = Assert.IsType<BlobAlignCheck>(s.GetCheck(a)).Reference!.Value;
        Assert.Equal(330.4, pose.X, 0.5);
        Assert.Equal(256.0, pose.Y, 0.5);
        Assert.Equal(-0.07, pose.Angle, 0.5);
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
        s.SetRoi(a, VisionRoi.Rect(194, 251, 60, 60));
        s.UpdateCheck(a, JsonNode.Parse("""{"Blob":{"Mode":"Fixed","Threshold":80,"Invert":true,"MinArea":300},"Min":1,"Max":1,"Code":"HOLE"}""")!.AsObject());

        var ok = await s.RunImageAsync(Samples.Bracket("01_good.bmp"));
        var ng = await s.RunImageAsync(Samples.Bracket("02_missinghole.bmp"));
        var missing = await s.RunImageAsync(Path.Combine(dir.Path, "nope.png"));

        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("Ok", ok.State);
        var row = Assert.Single(ok.Values);
        Assert.Equal(("왼쪽 구멍", 1.0, true), (row.Check, row.Value, row.Ok));
        Assert.Equal("Ng", ng.State);
        Assert.Equal(["HOLE"], ng.Codes);
        Assert.False(missing.Ok);
    }
}
