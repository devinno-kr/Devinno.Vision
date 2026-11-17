using System.Text.Json.Nodes;
using Devinno.UI;
using Devinno.UI.App;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.Studio.Ui;
using SkiaSharp;

namespace Devinno.Vision.Studio.Tests;

// 양품 모델 기준 — 명령 층 · 화면. 특징 모델은 테스트용 작은 모델(64×64, 무작위 가중치)이라 판정 성능은 보지 않는다
public class GoodModelStudioTests
{
    private static string Tiny => Path.Combine(AppContext.BaseDirectory, "Models", "tiny_features.onnx");

    private static StudioSession NewSession() => new() { FeaturesPath = Tiny, FeatureInputSize = 64 };

    // 양품만 든 폴더 (프로젝트 폴더 밖)
    private static string Goods(TempDir dir)
    {
        var goods = dir.Sub("goods");
        foreach (var f in Directory.GetFiles(Samples.PlateDir, "*_good.bmp"))
            File.Copy(f, Path.Combine(goods, Path.GetFileName(f)));
        return goods;
    }

    private static string Ok(CommandResult r)
    {
        Assert.True(r.Ok, r.Message);
        return r.Message;
    }

    // Plate 로직 + 양품 폴더
    private static StudioSession Prepared(TempDir dir)
    {
        var s = NewSession();
        PlateStudioTests.BuildPlate(s, dir.Sub("project"));
        Ok(s.SetGoodFolder(Goods(dir)));
        return s;
    }

    [Fact]
    public async Task Training_sets_the_model_marks_the_logic_changed_and_runs()
    {
        using var dir = new TempDir();
        var s = Prepared(dir);
        Ok(s.SaveProject());

        var msg = Ok(await s.TrainGoodModelAsync());

        Assert.Contains("양품 모델 학습됨: 8장 학습", msg);
        Assert.Contains("주의: 양품이 9장뿐입니다", msg);
        Assert.Contains("주의: 전체 이미지로 학습했습니다", msg);
        Assert.True(s.IsDirty);
        Assert.False(s.IsTraining);
        var summary = Assert.IsType<GoodTrainingSummary>(s.GoodSummary);
        Assert.Equal((9, 8, 1), (summary.Images, summary.Learned, summary.Calibration));
        Assert.Equal(s.Project!.GoodModel!.Max, summary.Max);

        var run = await s.RunImageAsync(Samples.Plate("01_good.bmp"));
        Assert.True(run.Ok, run.Message);
        Assert.Contains(run.Values, v => v.Check == VisionRecipe.GoodModelStepName && v.Name == "양품 모델 점수");
        Assert.True(run.Ms > 0);

        var test = await s.RunTestAsync(0);
        Assert.True(test.Ok, test.Message);
        Assert.Equal(18, test.Total);
    }

    [Fact]
    public async Task Commands_are_refused_while_training_and_cancel_keeps_the_old_state()
    {
        using var dir = new TempDir();
        var s = Prepared(dir);
        CommandResult? during = null;
        RunResult? runDuring = null;
        var progress = new Relay<GoodModelProgress>(_ =>
        {
            if (during is not null)
                return;
            during = s.AddCheck("Brightness", "밝기");
            runDuring = s.RunImageAsync(Samples.Plate("01_good.bmp")).GetAwaiter().GetResult();
            Assert.True(s.IsTraining);
            Assert.True(s.CancelTraining().Ok);
        });

        var r = await s.TrainGoodModelAsync(progress);

        Assert.False(during!.Ok);
        Assert.Contains("학습하는 중", during.Message);
        Assert.False(runDuring!.Ok);
        Assert.False(r.Ok);
        Assert.Contains("취소", r.Message);
        Assert.Null(s.Project!.GoodModel);
        Assert.False(s.IsTraining);
        Assert.False(s.CancelTraining().Ok);
    }

    [Fact]
    public async Task With_a_good_model_new_standard_checks_name_the_defect_and_length_still_judges()
    {
        using var dir = new TempDir();
        var s = Prepared(dir);
        Ok(await s.TrainGoodModelAsync());

        var hole = s.AddCheck("BlobCount", "가운데 구멍");
        var len = s.AddCheck("Length", "폭");

        Assert.Contains("사유 검사", Ok(hole));
        Assert.Equal(StepRole.Reason, s.GetCheck(hole.Id!)!.Role);
        Assert.Equal(StepRole.Judge, s.GetCheck(len.Id!)!.Role);

        // 파일에 쓰지 않는 기본값(Judge)도 속성 편집으로 바꿀 수 있다
        var left = s.Checks.Single(c => c.Name == "왼쪽 구멍");
        Ok(s.UpdateCheck(left.Id, JsonNode.Parse("""{"Role":"Reason"}""")!.AsObject()));
        Assert.Equal(StepRole.Reason, s.GetCheck(left.Id)!.Role);
        Ok(s.UpdateCheck(left.Id, JsonNode.Parse("""{"Role":"Judge"}""")!.AsObject()));
        Assert.Equal(StepRole.Judge, s.GetCheck(left.Id)!.Role);
    }

    [Fact]
    public async Task Teaching_again_after_training_asks_to_train_again()
    {
        using var dir = new TempDir();
        var s = Prepared(dir);
        Ok(await s.TrainGoodModelAsync());
        var align = s.Checks[0];
        Ok(s.UpdateCheck(align.Id, JsonNode.Parse("""{"Blob":{"MinArea":15000}}""")!.AsObject()));

        var run = await s.RunImageAsync(Samples.Plate("01_good.bmp"));
        var test = await s.RunTestAsync(0);

        Assert.False(run.Ok);
        Assert.Contains("다시 학습", run.Message);
        Assert.False(test.Ok);
        Assert.Contains("다시 학습", test.Message);
        Assert.False(s.Project!.GoodModelIsCurrent);

        Ok(await s.TrainGoodModelAsync());
        Assert.True((await s.RunImageAsync(Samples.Plate("01_good.bmp"))).Ok);
    }

    [Fact]
    public async Task A_project_whose_model_folder_is_missing_opens_and_says_why_it_cannot_run()
    {
        using var dir = new TempDir();
        var s = Prepared(dir);
        Ok(await s.TrainGoodModelAsync());
        Ok(s.SaveProject());
        var folder = s.Project!.Folder;
        Directory.Delete(Path.Combine(folder, s.Project.GoodModel!.Path), recursive: true);

        var again = NewSession();
        Ok(again.OpenProject(folder));
        var run = await again.RunImageAsync(Samples.Plate("01_good.bmp"));

        Assert.False(run.Ok);
        Assert.Contains("양품 모델이 없습니다", run.Message);
        Assert.NotNull(again.GoodFolder);   // 양품 폴더는 goodset.json 에서 돌아온다 — 다시 학습할 수 있다
        Ok(await again.TrainGoodModelAsync());
        Assert.True((await again.RunImageAsync(Samples.Plate("01_good.bmp"))).Ok);
    }

    [Fact]
    public void Good_folder_warns_about_test_folders_and_extra_good_images_are_kept()
    {
        using var dir = new TempDir();
        var s = NewSession();
        PlateStudioTests.BuildPlate(s, dir.Sub("project"));   // 테스트 폴더 = Samples/Plate

        var overlap = Ok(s.SetGoodFolder(Samples.PlateDir));
        Assert.Contains("주의: 테스트 폴더", overlap);

        Ok(s.SetGoodFolder(Goods(dir)));
        Ok(s.AddGoodImage(Samples.Plate("02_missinghole.bmp")));
        Assert.False(s.AddGoodImage(Samples.Plate("02_missinghole.bmp")).Ok);   // 두 번은 안 된다
        Assert.Equal(10, s.GoodImages().Count);

        Ok(s.SaveProject());
        var again = NewSession();
        Ok(again.OpenProject(s.Project!.Folder));
        Assert.Equal(10, again.GoodImages().Count);

        Assert.False(s.SetGoodFolder(Path.Combine(dir.Path, "none")).Ok);
        Assert.False(s.SetGoodFolder(s.Project.Folder).Ok);   // 프로젝트 폴더는 안 된다
    }

    [Fact]
    public async Task Training_without_a_feature_model_or_good_images_says_what_to_do()
    {
        using var dir = new TempDir();
        var s = NewSession();
        PlateStudioTests.BuildPlate(s, dir.Sub("project"));

        var noGoods = await s.TrainGoodModelAsync();
        Assert.Contains("양품 폴더를 먼저", noGoods.Message);

        Ok(s.SetGoodFolder(Goods(dir)));
        s.FeaturesPath = Path.Combine(dir.Path, "none.onnx");
        var noFeatures = await s.TrainGoodModelAsync();
        Assert.False(noFeatures.Ok);
        Assert.Contains("export_features.py", noFeatures.Message);
    }

    [Fact]
    public async Task Removing_the_model_deletes_its_folder_on_save()
    {
        using var dir = new TempDir();
        var s = Prepared(dir);
        Ok(await s.TrainGoodModelAsync());
        var path = Path.Combine(s.Project!.Folder, s.Project.GoodModel!.Path);
        Ok(s.SaveProject());

        Ok(s.RemoveGoodModel());
        Assert.True(Directory.Exists(path));   // 저장 전에는 남는다 (되돌릴 수 있게)
        Ok(s.SaveProject());

        Assert.False(Directory.Exists(path));
        Assert.False(s.RemoveGoodModel().Ok);
    }

    [Fact]
    public async Task Model_tab_trains_from_the_button()
    {
        using var dir = new TempDir();
        var session = Prepared(dir);
        var app = StudioApp.Create(session, new NullDialogs());
        app.Surface.Layout(1280, 800);
        var edit = app.Pages.OfType<EditPage>().Single();

        edit.Tab = RefTab.Model;
        app.Invalidate();
        app.Surface.Layout(1280, 800);
        Assert.False(edit.CannotTrain);
        var r = edit.TrainBounds;
        Assert.False(r.IsEmpty, "학습 버튼이 화면에 없습니다");
        app.Surface.PointerDown(new SKPoint(r.MidX, r.MidY));
        app.Surface.PointerUp(new SKPoint(r.MidX, r.MidY));
        app.Surface.Layout(1280, 800);
        for (var i = 0; i < 300 && (session.IsTraining || session.Project!.GoodModel is null); i++)
        {
            await Task.Delay(50);
            app.Surface.Layout(1280, 800);   // 화면은 프레임마다 처리한다
        }

        Assert.NotNull(session.Project!.GoodModel);
        Assert.StartsWith("학습됨", edit.GoodStatusText);
        Assert.Equal("양품 모델", edit.GoodBadge);
        Assert.Contains("양품 9장", edit.GoodFolderText);
    }

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

// ROI 가 빈 검사 · 양품 모델 영역도 이미지에서 끌어 그릴 수 있어야 한다 (새 검사에 ROI 가 안 그려지던 문제)
public class RoiDrawingTests
{
    private static void Drag(DvApp app, SKRect view, float dx0, float dy0, float dx1, float dy1)
    {
        Png.Render(app.Surface, 1280, 800);   // 뷰는 그릴 때 이미지 배치를 잡는다 (Layout 만으로는 끌기를 받지 않음)
        var a = new SKPoint(view.MidX + dx0, view.MidY + dy0);
        var b = new SKPoint(view.MidX + dx1, view.MidY + dy1);
        app.Surface.PointerDown(a);
        app.Surface.PointerMove(b);
        app.Surface.PointerUp(b);
        app.Surface.Layout(1280, 800);
    }

    [Fact]
    public void A_new_check_without_a_roi_gets_one_by_dragging()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);
        Assert.True(s.SetBaseImage(Samples.Plate("01_good.bmp")).Ok);
        var id = s.AddCheck("BlobCount", "좌상 구멍").Id!;
        var app = StudioApp.Create(s, new NullDialogs());
        var edit = app.Pages.OfType<EditPage>().Single();
        edit.SelectedId = id;
        app.Invalidate();
        app.Surface.Layout(1280, 800);
        Assert.True(s.GetCheck(id)!.Roi.IsEmpty);

        Drag(app, edit.ViewBounds, -60, -40, 20, 30);

        var roi = s.GetCheck(id)!.Roi;
        Assert.False(roi.IsEmpty);
        Assert.True(roi.Width > 10 && roi.Height > 10, roi.ToString());
    }

    [Fact]
    public void An_existing_roi_is_only_moved_or_resized_and_stray_clicks_change_nothing()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);
        Assert.True(s.SetBaseImage(Samples.Plate("01_good.bmp")).Ok);
        var id = s.AddCheck("BlobCount", "구멍").Id!;
        var set = VisionRoi.Rect(300, 220, 60, 40);   // 이미지 가운데 근처
        Assert.True(s.SetRoi(id, set).Ok);
        Assert.True(s.SaveProject().Ok);
        var app = StudioApp.Create(s, new NullDialogs());
        var edit = app.Pages.OfType<EditPage>().Single();
        edit.SelectedId = id;
        app.Invalidate();
        app.Surface.Layout(1280, 800);
        var v = edit.ViewBounds;

        Drag(app, v, -v.Width / 2 + 20, -v.Height / 2 + 20, -v.Width / 2 + 60, -v.Height / 2 + 60);   // ROI 바깥에서 끌기
        Assert.Equal(set, s.GetCheck(id)!.Roi);

        Drag(app, v, 0, 0, 0, 0);   // ROI 안을 누르고 그대로 뗌
        Assert.Equal(set, s.GetCheck(id)!.Roi);
        Assert.False(s.IsDirty);

        Drag(app, v, 0, 0, 30, 0);   // 안쪽을 끌면 옮겨진다
        var moved = s.GetCheck(id)!.Roi;
        Assert.True(moved.X > set.X, moved.ToString());
        Assert.Equal((set.Width, set.Height), (moved.Width, moved.Height));
    }

    [Fact]
    public void A_click_without_dragging_does_not_make_a_tiny_roi()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);
        Assert.True(s.SetBaseImage(Samples.Plate("01_good.bmp")).Ok);
        var id = s.AddCheck("BlobCount", "구멍").Id!;
        var app = StudioApp.Create(s, new NullDialogs());
        var edit = app.Pages.OfType<EditPage>().Single();
        edit.SelectedId = id;
        app.Invalidate();
        app.Surface.Layout(1280, 800);

        Drag(app, edit.ViewBounds, 0, 0, 1, 1);

        Assert.True(s.GetCheck(id)!.Roi.IsEmpty);
    }

    [Fact]
    public void The_good_model_region_is_drawn_by_dragging()
    {
        using var dir = new TempDir();
        var s = new StudioSession();
        Assert.True(s.NewProject(dir.Sub("p"), "t").Ok);
        Assert.True(s.SetBaseImage(Samples.Plate("01_good.bmp")).Ok);
        var app = StudioApp.Create(s, new NullDialogs());
        var edit = app.Pages.OfType<EditPage>().Single();
        edit.Tab = RefTab.Model;
        app.Invalidate();
        app.Surface.Layout(1280, 800);

        Drag(app, edit.ViewBounds, -80, -50, 80, 50);

        Assert.False(s.GoodRoi.IsEmpty);
    }
}
