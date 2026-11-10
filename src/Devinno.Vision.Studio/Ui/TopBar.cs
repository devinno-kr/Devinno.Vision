using Devinno.UI.App;
using Devinno.Vision.Studio.Session;
using SkiaSharp;

namespace Devinno.Vision.Studio.Ui;

/// <summary>위쪽 바 (창 전체 폭) — 새 로직 · 열기 · 저장 · 로직 이름 · 편집/테스트 전환. 결과는 상태 줄에.</summary>
public sealed partial class TopBar : DvBar
{
    private readonly StudioSession _session;
    private readonly IFileDialogs _dialogs;
    private readonly StatusBar _status;

    public TopBar(StudioSession session, IFileDialogs dialogs, StatusBar status)
    {
        _session = session;
        _dialogs = dialogs;
        _status = status;
    }

    public string Title => _session.Project is { } p ? p.Name : "Devinno Vision Studio";
    public string FolderText => _session.Project?.Folder ?? "로직이 없습니다 — [새 로직] 또는 [열기]";
    public bool NoProject => _session.Project is null;

    public string EditClass => TabClass<EditPage>();
    public string TestClass => TabClass<TestPage>();

    internal SKRect OpenBounds => btnOpen.Bounds;
    internal SKRect NewBounds => btnNew.Bounds;
    internal SKRect SaveBounds => btnSave.Bounds;

    public async void New()
    {
        try
        {
            if (_dialogs.PickFolder() is not { } folder)
            {
                _status.Ok("새 로직: 취소함");
                return;
            }
            if (await App.InputBox("새 로직 이름", Path.GetFileName(folder.TrimEnd('\\', '/'))) is not { } name)
            {
                _status.Ok("새 로직: 취소함");
                return;
            }
            _status.Report(_session.NewProject(folder, name));
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    public void Open()
    {
        if (_dialogs.PickFolder() is { } folder)
            _status.Report(_session.OpenProject(folder));
        else
            _status.Ok("열기: 취소함");
    }

    public void Save() => _status.Report(_session.SaveProject());

    public void ShowEdit() => App.Show<EditPage>();

    public void ShowTest() => App.Show<TestPage>();

    private string TabClass<T>() where T : DvPage => App.CurrentPage is T ? "tab tab-active" : "tab";
}
