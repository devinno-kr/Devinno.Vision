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
    public bool IsDirty => _session.IsDirty;

    public string EditClass => TabClass<EditPage>();
    public string TestClass => TabClass<TestPage>();

    internal SKRect OpenBounds => btnOpen.Bounds;
    internal SKRect NewBounds => btnNew.Bounds;
    internal SKRect SaveBounds => btnSave.Bounds;

    public async void New()
    {
        try
        {
            if (await ResolveUnsavedAsync("새 로직을 만들기") is not { } discard)
            {
                _status.Ok("새 로직: 취소함");
                return;
            }
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
            _status.Report(_session.NewProject(folder, name, discard));
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    public async void Open()
    {
        try
        {
            if (await ResolveUnsavedAsync("다른 로직을 열기") is not { } discard)
            {
                _status.Ok("열기: 취소함");
                return;
            }
            if (_dialogs.PickFolder() is { } folder)
                _status.Report(_session.OpenProject(folder, discard));
            else
                _status.Ok("열기: 취소함");
        }
        catch (Exception ex)
        {
            _status.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 저장 안 된 변경이 있으면 묻는다: [예] 저장하고 진행 · [아니요] 변경을 버리고 진행 · [취소] 멈춤.
    /// 돌려주는 값 = 진행할 때 변경을 버릴지 (null = 멈춤). 변경이 없으면 묻지 않고 false.
    /// </summary>
    private async Task<bool?> ResolveUnsavedAsync(string action)
    {
        if (!_session.IsDirty)
            return false;
        var answer = await App.MessageBox($"저장하지 않은 변경이 있습니다: {_session.Project?.Name}\n\n{action} 전에 저장할까요?\n[아니요] 를 누르면 변경을 버립니다.", "저장 안 됨", DvMessageButtons.YesNoCancel);
        if (answer == DvDialogResult.Yes)
        {
            var saved = _session.SaveProject();
            _status.Report(saved);
            return saved.Ok ? false : null;   // 저장 실패면 진행하지 않는다
        }
        return answer == DvDialogResult.No ? true : null;
    }

    public void Save() => _status.Report(_session.SaveProject());

    /// <summary>저장된 로직을 레시피 패키지(.dvpkg)로 — 현장 앱이 여는 파일.</summary>
    public void Pack()
    {
        if (_session.Project is not { } p)
            return;
        if (_dialogs.PickPackage($"{(string.IsNullOrWhiteSpace(p.Name) ? "recipe" : p.Name)}.dvpkg") is { } file)
            _status.Report(_session.PackProject(file));
        else
            _status.Ok("패키지: 취소함");
    }

    public void ShowEdit() => App.Show<EditPage>();

    public void ShowTest() => App.Show<TestPage>();

    private string TabClass<T>() where T : DvPage => App.CurrentPage is T ? "tab tab-active" : "tab";
}
