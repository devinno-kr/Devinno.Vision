using System.Runtime.CompilerServices;
using Devinno.UI;
using Devinno.UI.App;
using Devinno.Vision.Studio.Model;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.Studio.Ui;
using SkiaSharp;

namespace Devinno.Vision.Studio.Tests;

// 헤드리스로 앱을 실제 조작한다: 버튼을 Bounds 로 누르고, 대화상자는 그 안의 버튼을 누르고, 프레임을 돌린다.
// 시나리오 ①~④ 의 화면 PNG 는 .superpowers/sdd/2026-11-11-studio-ux/ 에 남긴다 (눈으로 확인용).
public class UiTests
{
    private const int W = 1280, H = 800;

    /// <summary>고르기 창 대신 미리 넣어 둔 경로를 차례로 돌려준다.</summary>
    private sealed class FakeDialogs : IFileDialogs
    {
        public Queue<string?> Images { get; } = new();
        public Queue<string?> Folders { get; } = new();
        public string? PickImage() => Images.Count > 0 ? Images.Dequeue() : null;
        public string? PickFolder() => Folders.Count > 0 ? Folders.Dequeue() : null;
    }

    private sealed class ThrowingDialogs : IFileDialogs
    {
        public string? PickImage() => throw new IOException("dialog broke");
        public string? PickFolder() => throw new IOException("dialog broke");
    }

    private sealed class Studio
    {
        public Studio(StudioSession? session = null, IFileDialogs? dialogs = null)
        {
            Session = session ?? new StudioSession();
            Dialogs = dialogs as FakeDialogs ?? new FakeDialogs();
            App = StudioApp.Create(Session, dialogs ?? Dialogs);
            App.Surface.UseNativeEditor = true;   // 실제 창(WinForms ViewControl)과 같게 — 화면 키보드 없음
            App.HandlerError += (_, e) => Errors.Add(e);
            Frame();
        }

        public StudioSession Session { get; }
        public FakeDialogs Dialogs { get; }
        public DvApp App { get; }
        public List<DvHandlerErrorEventArgs> Errors { get; } = [];
        public CheckListBar List => (CheckListBar)App.Left!;
        public TopBar Top => (TopBar)App.Top!;
        public StatusBar Status => (StatusBar)App.Bottom!;
        public EditPage Edit => App.Pages.OfType<EditPage>().Single();
        public TestPage Test => App.Pages.OfType<TestPage>().Single();

        public void Frame() => App.Surface.Layout(W, H);

        public void Press(SKRect r)
        {
            Assert.False(r.IsEmpty, "누를 버튼이 화면에 없습니다");
            App.Surface.PointerDown(new SKPoint(r.MidX, r.MidY));
            App.Surface.PointerUp(new SKPoint(r.MidX, r.MidY));
            Frame();
        }

        /// <summary>화면을 새로 그린 뒤 그 버튼의 Bounds 를 읽어 누른다.</summary>
        public void Tap(Func<SKRect> bounds)
        {
            App.Invalidate();
            Frame();
            Press(bounds());
        }

        /// <summary>대화상자의 버튼을 누른다 (프레임이 대화상자를 배치한 뒤).</summary>
        public void PressDialog(string id)
        {
            Until(() => App.Dialogs.Top?.BoundsOf(id) is not null);
            Press(App.Dialogs.Top!.BoundsOf(id)!.Value);
        }

        /// <summary>await 뒤 이어지는 코드는 다음 프레임에 돈다 — 조건이 될 때까지 프레임을 돌린다.</summary>
        public void Until(Func<bool> done, int ms = 8000)
        {
            var end = Environment.TickCount64 + ms;
            while (!done())
            {
                Assert.True(Environment.TickCount64 < end, $"화면이 기다린 상태가 되지 않았습니다 (상태 줄: {Status.Notice}, 대화상자: {App.Dialogs.Top?.GetType().Name})");
                Thread.Sleep(15);
                Frame();
            }
        }

        public byte[] Png(string? scenario = null, [CallerFilePath] string source = "")
        {
            App.Invalidate();
            var png = Tests.Png.Render(App.Surface, W, H);
            if (scenario is not null)
            {
                // tests/Devinno.Vision.Studio.Tests/UiTests.cs → 저장소 루트
                var dir = Path.Combine(Path.GetDirectoryName(source)!, "..", "..", ".superpowers", "sdd", "2026-11-11-studio-ux");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, scenario), png);
            }
            return png;
        }

        public string Errs => string.Join("\n", Errors.Select(e => $"{e.Kind} {e.Member}: {e.Exception}"));
    }

    [Fact]
    public void App_renders_empty_edit_page()
    {
        var s = new Studio();

        var png = s.Png("0-start.png");

        Assert.True(png.Length > 1000);
        Assert.Contains("새 로직", s.Edit.Hint);
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    [Fact]
    public void Top_bar_spans_the_window_and_the_list_starts_below_it()
    {
        var s = new Studio();
        var list = s.List.AddBounds;   // 목록 바 맨 위 줄의 버튼
        var open = s.Top.OpenBounds;

        Assert.True(open.Left < 260, "위 바의 첫 버튼이 창 왼쪽 끝 근처에 있어야 한다 (목록 바가 위 바를 먹지 않음)");
        Assert.True(list.Top >= 56, $"목록 바는 위 바(높이 56) 아래에서 시작해야 한다: {list.Top}");
    }

    // ① 새 로직 만들어 티칭 — 새 로직 → 기준 이미지 → 검사 추가 → 티칭 → 실행(오버레이) → 다른 이미지 실행 → 저장
    [Fact]
    public void Scenario1_new_logic_and_teach()
    {
        using var dir = new TempDir();
        var s = new Studio();
        s.Dialogs.Folders.Enqueue(dir.Sub("브래킷"));
        s.Dialogs.Images.Enqueue(Samples.Bracket("01_good.bmp"));
        s.Dialogs.Images.Enqueue(Samples.Bracket("02_missinghole.bmp"));

        s.Tap(() => s.Top.NewBounds);                  // [새 로직] → 폴더 → 이름 입력 창
        s.PressDialog("ok");                       // 이름은 폴더 이름 그대로 (실제 창처럼 확인 버튼)
        s.Until(() => s.Session.Project is not null);
        Assert.False(s.Status.IsError, s.Status.Notice);
        Assert.Contains("기준 이미지", s.Edit.Hint);
        s.Png("1-1-new-logic.png");

        s.Until(() => s.App.Dialogs.Top is null);
        s.Tap(() => s.Edit.OpenBaseBounds);            // 빈 화면 가운데 [기준 이미지 열기]
        Assert.True(s.Session.BaseImageFile is not null, $"상태 줄: {s.Status.Notice} / 버튼 {s.Edit.OpenBaseBounds}");
        Assert.Contains("추가", s.Edit.Hint);

        s.Tap(() => s.List.AddBounds);                 // [추가] → 대화상자
        var dialog = Assert.IsType<AddCheckDialog>(s.App.Dialogs.Top);
        dialog.CheckName = "정렬";
        dialog.Kind = "BlobAlign";
        s.App.Invalidate();
        s.Frame();
        s.PressDialog("ok");
        s.Until(() => s.Session.Checks.Count == 1);
        s.Edit.BlobMinArea = 20000;
        Assert.Contains("기준 티칭", s.Edit.Hint);
        s.Tap(() => s.Edit.TeachBounds);
        Assert.NotNull(((BlobAlignCheck)s.Session.Checks[0]).Reference);
        Assert.Contains("기준 위치", s.Status.Notice);

        var hole = s.Session.AddCheck("BlobCount", "왼쪽 구멍").Id!;     // (ROI 끌기는 헤드리스로 재현하지 않고 명령으로)
        s.Session.SetRoi(hole, VisionRoi.Rect(194, 251, 60, 60));
        s.List.SelectedId = hole;
        s.Edit.BlobMode = ThresholdMode.Fixed;
        s.Edit.BlobThreshold = 80;
        s.Edit.BlobInvert = true;
        s.Edit.BlobMinArea = 300;
        s.Edit.CountMin = 1;
        s.App.Invalidate();
        s.Frame();
        s.Png("1-2-check-selected.png");

        s.Tap(() => s.Edit.RunBounds);                 // [이 이미지로 실행] → 이미지 위 오버레이
        s.Until(() => s.Edit.ShowingResult && s.Edit.HasValues);
        Assert.Contains("OK", s.Edit.LastRun);
        s.Png("1-3-run-overlay.png");

        s.Tap(() => s.Edit.RunOtherBounds);            // [이미지 열어 실행] → 02_missinghole
        s.Until(() => s.Edit.LastRun.Contains("02_missinghole"));
        Assert.Contains("NG", s.Edit.LastRun);
        s.Png("1-4-run-other-ng.png");

        s.Tap(() => s.Top.SaveBounds);
        Assert.Contains("저장됨", s.Status.Notice);
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    // ② 폴더로 테스트하고 한 장씩 훑기 — 폴더 추가(라벨 규칙 고르기) → 실행 전 목록 → 한 장 → 다음 · → 키 → 실행 → 필터
    [Fact]
    public void Scenario2_test_folder_and_browse()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        session.RemoveTestSet(0);
        var s = new Studio(session);
        s.App.Show(s.Test);
        s.Frame();
        s.Png("2-0-no-folder.png");

        s.Dialogs.Folders.Enqueue(Samples.BracketDir);
        s.Tap(() => s.Test.AddSetBounds);              // [폴더 추가] → 라벨 읽는 법 고르기
        s.PressDialog("ok");                       // 첫 항목 = 파일 이름
        s.Until(() => s.Test.Rows.Count == 18);
        Assert.All(s.Test.Rows, r => Assert.Equal("—", r.Result));   // 실행 전에도 목록이 뜬다
        Assert.True(s.Test.NoSelection);
        s.Png("2-1-images-before-run.png");

        s.Test.SelectedRow = s.Test.Rows[1];       // 표에서 한 장 고르기
        s.Until(() => s.Test.Showing.IsCompleted);
        s.Frame();
        Assert.Equal("2 / 18", s.Test.Position);
        Assert.Contains("NG", s.Test.Detail);
        s.Png("2-2-browse-one.png");

        s.Tap(() => s.Test.NextBounds);                // [다음]
        s.Until(() => s.Test.Showing.IsCompleted);
        Assert.True(StudioApp.ArrowKey(s.App, next: true));   // → 키
        s.Until(() => s.Test.Showing.IsCompleted);
        s.Frame();
        Assert.Equal("4 / 18", s.Test.Position);

        s.Tap(() => s.Test.RunBounds);                 // [실행]
        s.Until(() => s.Test.HasReport && !s.Test.Running);
        s.Until(() => s.Test.Showing.IsCompleted);
        s.Frame();
        Assert.Equal("18/18 맞음", s.Test.Summary);
        Assert.Equal(4, s.Test.Labels.Count);
        Assert.Equal("4 / 18", s.Test.Position);   // 고른 이미지 유지
        Assert.All(s.Test.Rows, r => Assert.Equal("맞음", r.Judge));
        s.Png("2-3-after-run.png");

        s.Test.Filter = TestFilter.Ng;
        s.Frame();
        Assert.Equal(9, s.Test.Rows.Count);
        s.Test.Filter = TestFilter.Wrong;
        s.Frame();
        Assert.True(s.Test.NoRows);
        s.Png("2-4-filter-wrong-empty.png");
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    // ③ NG 원인 찾아 고치기 — 길이 공칭값을 틀리게 → 실행 → [틀린 것] → 한 장 → [이 검사 고치기] → 편집에서 그 검사
    [Fact]
    public void Scenario3_find_cause_and_fix()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        var len = session.Checks.Single(c => c.Name == "길이").Id;
        session.UpdateCheck(len, System.Text.Json.Nodes.JsonNode.Parse("""{"Nominal":380}""")!.AsObject());
        var s = new Studio(session);
        s.App.Show(s.Test);
        s.Frame();
        s.Tap(() => s.Test.RunBounds);
        s.Until(() => s.Test.HasReport && !s.Test.Running);

        s.Test.Filter = TestFilter.Wrong;
        s.Frame();
        Assert.True(s.Test.Rows.Count >= 9, $"틀린 것 {s.Test.Rows.Count}");   // 양품 9장이 모두 길이 NG
        s.Test.SelectedRow = s.Test.Rows[0];
        s.Until(() => s.Test.Showing.IsCompleted);
        s.Frame();
        Assert.Contains("원인 검사: 길이", s.Test.Detail);
        Assert.Contains("틀림", s.Test.Detail);
        s.Png("3-1-wrong-item.png");

        s.Tap(() => s.Test.FixBounds);
        Assert.Same(s.Edit, s.App.CurrentPage);
        Assert.Equal(len, s.List.SelectedId);
        Assert.True(s.Edit.IsLength);
        s.Edit.LengthNominal = 411;                // 고친다
        s.Frame();
        s.Png("3-2-fix-in-edit.png");
        Assert.True(s.Errors.Count == 0, s.Errs);
    }

    // ④ 검사 · 테스트 폴더 추가/삭제 — 확인 창 · 프로젝트 폴더 막기 · 라벨 없는 파일
    [Fact]
    public void Scenario4_add_and_delete_with_confirmation()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        var s = new Studio(session);
        var hole = session.Checks.Single(c => c.Name == "오른쪽 구멍").Id;
        s.List.SelectedId = hole;
        s.App.Invalidate();
        s.Frame();

        s.Tap(() => s.List.RemoveBounds);              // 짧게 누름 → 확인 창
        s.Until(() => s.App.Dialogs.Top is not null);
        s.Png("4-1-delete-confirm.png");
        s.PressDialog("no");                       // [아니요] → 그대로
        s.Until(() => s.App.Dialogs.Top is null);
        Assert.NotNull(session.GetCheck(hole));
        s.Tap(() => s.List.RemoveBounds);
        s.PressDialog("yes");                      // [예] → 삭제
        s.Until(() => session.GetCheck(hole) is null);
        Assert.Contains("삭제됨", s.Status.Notice);

        s.App.Show(s.Test);
        s.Frame();
        s.Dialogs.Folders.Enqueue(Path.Combine(dir.Path, "images"));   // 프로젝트 자신의 images 폴더
        s.Tap(() => s.Test.AddSetBounds);
        s.PressDialog("ok");
        s.Until(() => s.Status.IsError);
        Assert.Contains("프로젝트 폴더", s.Status.Notice);
        Assert.Single(session.TestSets);
        s.Png("4-2-own-folder-blocked.png");

        // 라벨 없는 파일 ('_' 없음) 이 섞인 폴더
        var mixed = dir.Sub("../mixed-" + Guid.NewGuid().ToString("N")[..6]);
        File.Copy(Samples.Bracket("01_good.bmp"), Path.Combine(mixed, "01_good.bmp"));
        File.Copy(Samples.Bracket("02_missinghole.bmp"), Path.Combine(mixed, "scan0002.bmp"));
        s.Dialogs.Folders.Enqueue(mixed);
        s.Tap(() => s.Test.AddSetBounds);
        s.PressDialog("ok");
        s.Until(() => session.TestSets.Count == 2 && s.Test.Rows.Count == 2);
        s.Tap(() => s.Test.RunBounds);
        s.Until(() => s.Test.HasReport && !s.Test.Running);
        Assert.Equal("1/1 맞음 · 라벨 없음 1장", s.Test.Summary);
        Assert.Contains(s.Test.Rows, r => r.Label == "라벨 없음" && r.Judge == "제외");
        s.Png("4-3-unlabeled.png");

        s.Tap(() => s.Test.RemoveSetBounds);           // 테스트 폴더 삭제도 확인 창
        s.PressDialog("yes");
        s.Until(() => session.TestSets.Count == 1);
        Directory.Delete(mixed, recursive: true);
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
    public void Editing_after_a_run_clears_the_stale_result()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        var s = new Studio(session);
        s.Tap(() => s.Edit.RunBounds);
        s.Until(() => s.Edit.ShowingResult && s.Edit.HasValues);

        session.SetRoi(session.Checks[1].Id, VisionRoi.Rect(190, 250, 60, 60));   // 로직이 바뀜
        s.Frame();

        Assert.False(s.Edit.ShowingResult);
        Assert.Contains("다시 실행", s.Edit.LastRun);
    }

    [Fact]
    public void Handler_exception_shows_in_status_line()
    {
        var s = new Studio(dialogs: new ThrowingDialogs());

        s.Tap(() => s.Top.OpenBounds);
        s.Frame();

        Assert.True(s.Status.IsError);
        Assert.Contains("dialog broke", s.Status.Notice);
    }
}
