using Devinno.UI.App;
using Devinno.UI.Components;
using Devinno.UI.Localization;
using Devinno.UI.Styling;
using Devinno.UI.WinForms;
using Devinno.Vision.Studio.Mcp;
using Devinno.Vision.Studio.Session;
using Devinno.Vision.Studio.Ui;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio;

/// <summary>Studio 앱 만들기 — 창 없이도 만들 수 있다 (테스트).</summary>
public static class StudioApp
{
    public static DvApp Create(StudioSession session, IFileDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dialogs);
        var registry = new ComponentRegistry();
        DvVisionView.Register(registry);
        var app = new DvApp { Theme = DvTheme.Dark, Registry = registry, Strings = new DvStrings { Language = "ko" } };
        var edit = new EditPage(session, dialogs);
        app.Pages.Add(edit);
        app.Pages.Add(new TestPage(session, dialogs, edit));
        app.Top = new TopBar(session, dialogs);
        app.Left = new CheckListBar(session, edit);
        app.Bottom = new StatusBar();
        session.ProjectChanged += app.Invalidate;   // MCP 가 바꿔도 다음 프레임에 다시 그린다
        app.HandlerError += (_, e) => SetStatus(app, $"오류: {e.Exception.Message}");   // 화면 명령이 조용히 죽지 않게
        return app;
    }

    /// <summary>아래 상태 줄 문구를 바꾼다 (어느 스레드에서든).</summary>
    public static void SetStatus(DvApp app, string text)
    {
        if (app.Bottom is StatusBar bar)
        {
            bar.Text = text;
            app.Invalidate();
        }
    }
}

internal static class Program
{
    public const int McpPort = 5199;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var session = new StudioSession();
        using var form = new Form { Text = "Devinno Vision Studio", ClientSize = new Size(1280, 800), StartPosition = FormStartPosition.CenterScreen };
        var app = StudioApp.Create(session, new WinFormsDialogs(form));
        form.Controls.Add(new ViewControl { Dock = DockStyle.Fill, App = app });

        // 창이 뜨기 전, 동기화 문맥이 없을 때 시작한다
        McpHost? mcp = null;
        try
        {
            mcp = McpHost.StartAsync(session, McpPort).GetAwaiter().GetResult();
            StudioApp.SetStatus(app, $"MCP: {mcp.Endpoint}");
        }
        catch (IOException)
        {
            StudioApp.SetStatus(app, $"MCP 꺼짐: 포트 {McpPort} 사용 중");
        }
        try
        {
            Application.Run(form);
        }
        finally
        {
            mcp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // WinForms 파일 · 폴더 고르기 — 창의 UI 스레드에서 연다
    private sealed class WinFormsDialogs(Form owner) : IFileDialogs
    {
        public string? PickImage() => OnUi(() =>
        {
            using var d = new OpenFileDialog { Title = "이미지 고르기", Filter = "이미지|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|모든 파일|*.*" };
            return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
        });

        public string? PickFolder() => OnUi(() =>
        {
            using var d = new FolderBrowserDialog { Description = "폴더 고르기", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
        });

        private string? OnUi(Func<string?> show) => owner.InvokeRequired ? (string?)owner.Invoke(show) : show();
    }
}
