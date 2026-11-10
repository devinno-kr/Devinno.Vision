using Devinno.UI.App;
using Devinno.UI.Components;
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
        var app = new DvApp { Theme = DvTheme.Dark, Registry = registry };
        app.Top = new TopBar();
        app.Bottom = new StatusBar();
        app.Pages.Add(new EditPage(session));
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
        var app = StudioApp.Create(session, new NullFileDialogs());
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

    // Task 8 에서 WinForms 구현으로 바꾼다
    private sealed class NullFileDialogs : IFileDialogs
    {
        public string? PickImage() => null;
        public string? PickFolder() => null;
    }
}
