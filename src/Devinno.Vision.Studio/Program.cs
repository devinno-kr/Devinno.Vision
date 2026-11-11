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
        var status = new StatusBar();
        var edit = new EditPage(session, dialogs, status);
        app.Pages.Add(edit);
        app.Pages.Add(new TestPage(session, dialogs, edit, status));
        // 배치 규칙 (DvApp): Top · Bottom = 창 전체 폭, Left = 그 사이 높이 → 검사 목록은 위 바 아래에서 시작한다
        app.Top = new TopBar(session, dialogs, status);
        app.Left = new CheckListBar(session, edit, status);
        app.Bottom = status;
        session.ProjectChanged += app.Invalidate;   // MCP 가 바꿔도 다음 프레임에 다시 그린다
        app.HandlerError += (_, e) => status.Fail(e.Exception.Message);   // 화면 명령이 조용히 죽지 않게
        return app;
    }

    /// <summary>아래 상태 줄의 MCP 상태를 바꾼다 (어느 스레드에서든).</summary>
    public static void SetStatus(DvApp app, string text)
    {
        if (app.Bottom is StatusBar bar)
        {
            bar.Mcp = text;
            app.Invalidate();
        }
    }

    /// <summary>창을 닫아도 되는가. 저장 안 된 변경이 있으면 <paramref name="ask"/> 로 묻는다: true = 저장하고 닫기, false = 버리고 닫기, null = 닫지 않기.</summary>
    public static bool CanClose(StudioSession session, Func<bool?> ask)
    {
        if (!session.IsDirty)
            return true;
        return ask() switch
        {
            true => session.SaveProject().Ok,   // 저장 실패면 닫지 않는다
            false => true,
            null => false,
        };
    }

    /// <summary>← → 키: 테스트 화면이 보이고 대화상자가 없을 때 이전/다음 이미지. 처리했으면 true.</summary>
    public static bool ArrowKey(DvApp app, bool next)
    {
        if (app.CurrentPage is not TestPage page || app.Dialogs.Top is not null)
            return false;
        if (next) page.Next();
        else page.Prev();
        app.Invalidate();
        return true;
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
        using var form = new StudioForm { Text = "Devinno Vision Studio", ClientSize = new Size(1280, 800), StartPosition = FormStartPosition.CenterScreen };
        var app = StudioApp.Create(session, new WinFormsDialogs(form));
        form.App = app;
        form.FormClosing += (_, e) =>
        {
            e.Cancel = !StudioApp.CanClose(session, () =>
                MessageBox.Show(form, $"저장하지 않은 변경이 있습니다: {session.Project?.Name}\n\n닫기 전에 저장할까요?\n[아니요] 를 누르면 변경을 버립니다.",
                    "저장 안 됨", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning) switch
                {
                    DialogResult.Yes => true,
                    DialogResult.No => false,
                    _ => null,
                });
            if (e.Cancel && session.IsDirty)
                app.Invalidate();
        };
        form.Controls.Add(new ViewControl { Dock = DockStyle.Fill, App = app });

        McpHost? mcp = null;
        try
        {
            mcp = McpHost.StartAsync(session, McpPort).GetAwaiter().GetResult();
            StudioApp.SetStatus(app, $"MCP {mcp.Endpoint.Authority}");
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

    // 화살표 키는 컨트롤이 길 찾기에 쓰기 전에 여기서 가로챈다 (Devinno.UI 페이지에는 키 이벤트가 없다)
    private sealed class StudioForm : Form
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DvApp? App { get; set; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (App is { } app && keyData is Keys.Left or Keys.Right && StudioApp.ArrowKey(app, keyData == Keys.Right))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
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

        public string? PickModel() => OnUi(() =>
        {
            using var d = new OpenFileDialog { Title = "ONNX 모델 고르기 (같은 폴더에 설명 .json 필요)", Filter = "ONNX 모델|*.onnx" };
            return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
        });

        private string? OnUi(Func<string?> show) => owner.InvokeRequired ? (string?)owner.Invoke(show) : show();
    }
}
