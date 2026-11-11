using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Tests;

// 패키지 — Studio 에서 만든 로직을 .dvpkg 로 묶고, 현장 앱처럼 라이브러리만으로 연다
public class PackStudioTests
{
    [Fact]
    public async Task Saved_logic_is_packed_and_opens_like_the_field_app_would()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        BracketStudioTests.BuildBracket(s, dir.Sub("project"));
        var file = Path.Combine(dir.Sub("out"), "bracket.dvpkg");

        var dirty = s.PackProject(file);
        Assert.False(dirty.Ok);
        Assert.Contains("먼저 저장", dirty.Message);

        Assert.True(s.SaveProject().Ok);
        var inside = s.PackProject(Path.Combine(s.Project!.Folder, "x.dvpkg"));
        Assert.False(inside.Ok);
        Assert.Contains("프로젝트 폴더 안", inside.Message);

        var r = s.PackProject(file);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("VisionRecipe.Load", r.Message);

        using var recipe = VisionRecipe.Load(file);
        Assert.Empty(recipe.TestSets);
        var job = recipe.CreateJob();
        using var img = VisionImage.Load(Samples.Bracket("02_missinghole.bmp"));
        using var result = await job.RunAsync(img);
        Assert.Equal(VisionState.Ng, result.State);
        Assert.Equal(["HOLE"], result.Codes);
    }
}
