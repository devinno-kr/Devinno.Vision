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
    public string FolderText => _session.Project?.Folder ?? Texts.T("top.noProject");

    /// <summary>화면 언어 ("ko" · "en") — 바꾸면 화면을 그 언어로 다시 그리고, 다음 실행에도 유지한다 (Texts 가 설정 파일에 저장).</summary>
    public string Language
    {
        get => Texts.Language;
        set => Texts.SetLanguage(value);
    }
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
            if (await ResolveUnsavedAsync("top.unsaved.new") is not { } discard)
            {
                _status.Ok(Texts.T("top.new.canceled"));
                return;
            }
            if (_dialogs.PickFolder() is not { } folder)
            {
                _status.Ok(Texts.T("top.new.canceled"));
                return;
            }
            if (await App.InputBox(Texts.T("top.new.name"), Path.GetFileName(folder.TrimEnd('\\', '/'))) is not { } name)
            {
                _status.Ok(Texts.T("top.new.canceled"));
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
            if (await ResolveUnsavedAsync("top.unsaved.open") is not { } discard)
            {
                _status.Ok(Texts.T("top.open.canceled"));
                return;
            }
            if (_dialogs.PickFolder() is { } folder)
                _status.Report(_session.OpenProject(folder, discard));
            else
                _status.Ok(Texts.T("top.open.canceled"));
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
    private async Task<bool?> ResolveUnsavedAsync(string questionKey)
    {
        if (!_session.IsDirty)
            return false;
        var answer = await App.MessageBox(Texts.T(questionKey, _session.Project?.Name), Texts.T("top.unsaved.title"), DvMessageButtons.YesNoCancel);
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
            _status.Ok(Texts.T("top.pack.canceled"));
    }

    public void ShowEdit() => App.Show<EditPage>();

    public void ShowTest() => App.Show<TestPage>();

    private string TabClass<T>() where T : DvPage => App.CurrentPage is T ? "tab tab-active" : "tab";
}
