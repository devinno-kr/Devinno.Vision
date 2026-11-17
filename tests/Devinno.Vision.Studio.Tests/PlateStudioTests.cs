using System.Text.Json.Nodes;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Tests;

// 완료 기준 — Plate 로직을 명령 층만으로 만들어 18 장 테스트 통과
public class PlateStudioTests
{
    private static string Ok(CommandResult r)
    {
        Assert.True(r.Ok, r.Message);
        return r.Id ?? "";
    }

    private static void Update(StudioSession s, string id, string json) => Ok(s.UpdateCheck(id, JsonNode.Parse(json)!.AsObject()));

    /// <summary>Plate 로직을 명령으로 만든다 (다른 테스트도 쓴다).</summary>
    internal static void BuildPlate(StudioSession s, string folder)
    {
        Ok(s.NewProject(folder, "플레이트"));
        Ok(s.SetBaseImage(Samples.Plate("01_good.bmp")));

        var align = Ok(s.AddCheck("BlobAlign", "정렬"));
        Update(s, align, """{"Blob":{"MinArea":20000}}""");
        Ok(s.TeachBase());

        const string hole = """{"Blob":{"Mode":"Fixed","Threshold":80,"Invert":true,"MinArea":300},"Min":1,"Max":1,"Code":"HOLE"}""";
        var left = Ok(s.AddCheck("BlobCount", "왼쪽 구멍"));
        Ok(s.SetRoi(left, VisionRoi.Rect(138, 130, 60, 60)));
        Update(s, left, hole);
        var right = Ok(s.AddCheck("BlobCount", "오른쪽 구멍"));
        Ok(s.SetRoi(right, VisionRoi.Rect(442, 130, 60, 60)));
        Update(s, right, hole);

        var tab = Ok(s.AddCheck("PixelCount", "노치"));
        Ok(s.SetRoi(tab, VisionRoi.Rect(238, 116, 36, 20)));
        Update(s, tab, """{"Low":100,"High":255,"Max":200,"Code":"NOTCH"}""");

        var len = Ok(s.AddCheck("Length", "길이"));
        Ok(s.SetRoi(len, VisionRoi.Rect(135, 312, 370, 16)));
        Update(s, len, """{"Polarity":"DarkObject","MinContrast":8,"Nominal":336,"Plus":4,"Minus":4,"Code":"LENGTH"}""");

        Ok(s.AddTestSet(Samples.PlateDir, LabelRule.FileName));
    }

    [Fact]
    public async Task Plate_logic_built_by_commands_passes_all_18()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        BuildPlate(s, dir.Path);

        var r = await s.RunTestAsync(0);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(18, r.Total);
        Assert.True(r.Correct == 18, string.Join("\n", r.Items.Where(i => i.Correct != true).Select(i => $"{i.File} {i.State} {string.Join(",", i.Codes)} {i.Error}")));
        Assert.Equal(3, r.Labels.Single(l => l.Label == "missinghole").Correct);
        Assert.Equal(3, r.Labels.Single(l => l.Label == "notchdefect").Correct);
        Assert.Equal(3, r.Labels.Single(l => l.Label == "distancedefect").Correct);
        Assert.Equal(9, r.Labels.Single(l => l.Label == "good").Correct);
    }
}
