using Devinno.UI;
using Devinno.UI.App;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.Studio.Ui;
using SkiaSharp;

namespace Devinno.Vision.Studio.Tests;

// 헤드리스: app.Surface.Layout · PointerDown/Up · 렌더. 앱은 프로세스에 하나만 창에 붙지만, 창 없이 만드는 것은 몇 개든 된다
public class UiTests
{
    private const int W = 1280, H = 800;

    private sealed class Studio
    {
        public Studio(StudioSession? session = null, IFileDialogs? dialogs = null)
        {
            Session = session ?? new StudioSession();
            App = StudioApp.Create(Session, dialogs ?? new NullDialogs());
            App.HandlerError += (_, e) => Errors.Add(e);
            Frame();
        }

        public StudioSession Session { get; }
        public DvApp App { get; }
        public List<DvHandlerErrorEventArgs> Errors { get; } = [];
        public CheckListBar List => (CheckListBar)App.Left!;
        public EditPage Edit => App.Pages.OfType<EditPage>().Single();
        public TestPage Test => App.Pages.OfType<TestPage>().Single();

        public void Frame() => App.Surface.Layout(W, H);

        public void Press(SKRect r)
        {
            App.Surface.PointerDown(new SKPoint(r.MidX, r.MidY));
            App.Surface.PointerUp(new SKPoint(r.MidX, r.MidY));
            Frame();
        }

        public byte[] Png(string? save = null)
        {
            var png = Tests.Png.Render(App.Surface, W, H);
            if (save is not null)
                File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, save), png);   // 눈으로 확인용
            return png;
        }

        public string Errs => string.Join("\n", Errors.Select(e => $"{e.Kind} {e.Member}: {e.Exception}"));
    }

    [Fact]
    public void App_renders_empty_edit_page()
    {
        var s = new Studio();

        var png = s.Png("edit-empty.png");

        Assert.True(png.Length > 1000);
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    [Fact]
    public void Add_check_from_list_bar_adds_to_project()
    {
        using var dir = new TempDir();
        var s = new Studio();
        s.Session.NewProject(dir.Path, "p");
        s.Frame();

        s.Press(s.List.AddBounds);
        s.Frame();
        var dialog = Assert.IsType<AddCheckDialog>(s.App.Dialogs.Top);
        dialog.CheckName = "왼쪽 구멍";
        dialog.Kind = "BlobCount";
        s.App.Invalidate();
        s.Frame();
        s.Press(dialog.BoundsOf("ok")!.Value);
        s.Frame();

        var check = Assert.Single(s.Session.Project!.Checks);
        Assert.Equal(("왼쪽 구멍", "BlobCount"), (check.Name, check.Kind));
        Assert.Equal(check.Id, s.List.SelectedId);   // 새 검사가 선택된다
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    [Fact]
    public void Selecting_a_check_shows_its_property_panel()
    {
        using var dir = new TempDir();
        var s = new Studio();
        s.Session.NewProject(dir.Path, "p");
        s.Session.SetBaseImage(Samples.Bracket("01_good.bmp"));
        var id = s.Session.AddCheck("BlobCount", "왼쪽 구멍").Id!;
        s.Session.SetRoi(id, VisionRoi.Rect(194, 251, 60, 60));
        s.Frame();

        s.List.SelectedId = id;
        s.App.Invalidate();
        s.Frame();

        Assert.True(s.Edit.IsBlobCount);
        Assert.False(s.Edit.IsLength);
        Assert.Equal(VisionRoi.Rect(194, 251, 60, 60), s.Edit.View.EditRoi);
        s.Edit.CountMax = 3;                                   // 속성 패널 값 → 명령
        Assert.Equal(3, ((Model.BlobCountCheck)s.Session.GetCheck(id)!).Max);
        Assert.True(s.Png("edit-blobcount.png").Length > 20_000);
        s.Edit.RunBase();
        SpinWait.SpinUntil(() => s.Edit.LastValues.Count > 0, 5000);
        Assert.Single(s.Edit.LastValues);
        s.App.Invalidate();   // 클릭이 아니라 직접 불렀으므로
        s.Png("edit-run.png");
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    [Fact]
    public async Task Mcp_edit_appears_on_screen()
    {
        using var dir = new TempDir();
        var s = new Studio();
        s.Session.NewProject(dir.Path, "p");
        s.Frame();
        var before = s.List.Rows.Count;

        await Task.Run(() => s.Session.AddCheck("Brightness", "밖에서"));   // 다른 스레드 (MCP 처럼)
        s.Frame();

        Assert.Equal(before + 1, s.List.Rows.Count);
        Assert.Contains(s.List.Rows, r => r.Text.Contains("밖에서"));
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    [Fact]
    public async Task Test_page_shows_report_rows()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        var s = new Studio(session);
        s.App.Show(s.Test);
        s.Frame();

        await s.Test.RunAsync();
        s.Frame();

        Assert.Equal(4, s.Test.Labels.Count);
        Assert.Equal("18/18 맞음", s.Test.Summary);
        Assert.Empty(s.Test.Failures);
        s.Png("test-report.png");
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    [Fact]
    public async Task Failure_shows_result_and_fix_opens_the_failing_check()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        var len = session.Checks.Single(c => c.Name == "길이").Id;
        session.UpdateCheck(len, System.Text.Json.Nodes.JsonNode.Parse("""{"Nominal":380}""")!.AsObject());   // 양품이 길이 NG 가 되게
        var s = new Studio(session);
        s.App.Show(s.Test);
        s.Frame();
        await s.Test.RunAsync();
        s.Frame();

        var first = s.Test.Failures[0];
        s.Test.SelectedFailure = first.Index;
        await s.Test.Showing;
        s.Frame();
        s.Png("test-failure.png");
        s.Test.Fix();
        s.Frame();

        Assert.Contains("길이", s.Test.FailureDetail);
        Assert.Equal(len, s.List.SelectedId);
        Assert.Same(s.Edit, s.App.CurrentPage);
        Assert.True(s.Errors.Count == 0, s.Errs);
    }
}
