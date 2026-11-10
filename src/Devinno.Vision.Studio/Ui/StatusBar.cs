using Devinno.UI.App;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Ui;

/// <summary>
/// 아래 상태 줄. 왼쪽 = 마지막 동작의 결과 (✓ 성공 · ⚠ 실패) — 모든 버튼이 여기에 결과를 남겨 누르면 반드시 반응이 보인다.
/// 오른쪽 = MCP 상태.
/// </summary>
public sealed partial class StatusBar : DvBar
{
    private readonly object _gate = new();
    private string _notice = "준비됨";
    private bool _error;
    private string _mcp = "";

    /// <summary>마지막 동작의 결과 문구.</summary>
    public string Notice { get { lock (_gate) return _notice; } }

    public bool IsError { get { lock (_gate) return _error; } }

    public string NoticeClass => IsError ? "notice notice-error" : "notice";

    public string NoticeIcon => IsError ? "fa-triangle-exclamation" : "fa-circle-check";

    public string IconClass => IsError ? "icon icon-error" : "icon";

    /// <summary>MCP 상태 (주소 · 꺼진 이유).</summary>
    public string Mcp { get { lock (_gate) return _mcp; } set { lock (_gate) _mcp = value ?? ""; } }

    public void Ok(string text) => Set(text, false);

    public void Fail(string text) => Set(text, true);

    /// <summary>명령 결과를 그대로 보여 준다.</summary>
    public void Report(CommandResult r)
    {
        if (r.Ok) Ok(r.Message);
        else Fail(r.Message);
    }

    private void Set(string text, bool error)
    {
        lock (_gate)
        {
            _notice = text;
            _error = error;
        }
        try { App.Invalidate(); }
        catch (InvalidOperationException) { }   // 아직 앱에 붙기 전
    }
}
