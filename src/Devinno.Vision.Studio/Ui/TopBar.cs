using Devinno.UI.App;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Ui;

/// <summary>위쪽 바 — 새 로직 · 열기 · 저장 · 로직 이름 · 편집/테스트 전환.</summary>
public sealed partial class TopBar : DvBar
{
    private readonly StudioSession _session;
    private readonly IFileDialogs _dialogs;

    public TopBar(StudioSession session, IFileDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    public string Title => _session.Project is { } p ? p.Name : "Devinno Vision Studio";
    public string FolderText => _session.Project?.Folder ?? "로직이 없습니다";
    public bool NoProject => _session.Project is null;
    public string Message { get; private set; } = "";
    public bool HasMessage => Message.Length > 0;

    public string EditClass => TabClass<EditPage>();
    public string TestClass => TabClass<TestPage>();

    public async void New()
    {
        if (_dialogs.PickFolder() is not { } folder)
            return;
        if (await App.InputBox("새 로직 이름", Path.GetFileName(folder.TrimEnd('\\', '/'))) is not { } name)
            return;
        Report(_session.NewProject(folder, name));
    }

    public void Open()
    {
        if (_dialogs.PickFolder() is { } folder)
            Report(_session.OpenProject(folder));
    }

    public void Save() => Report(_session.SaveProject());

    public void ShowEdit() => App.Show<EditPage>();

    public void ShowTest() => App.Show<TestPage>();

    private void Report(CommandResult r) => Message = r.Ok ? "" : r.Message;

    private string TabClass<T>() where T : DvPage => App.CurrentPage is T ? "tab tab-active" : "tab";
}
