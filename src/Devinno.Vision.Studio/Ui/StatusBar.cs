using Devinno.UI.App;

namespace Devinno.Vision.Studio.Ui;

/// <summary>아래쪽 상태 줄 (MCP 주소 · 꺼진 이유 등).</summary>
public sealed partial class StatusBar : DvBar
{
    private volatile string _text = "";

    public string Text { get => _text; set => _text = value ?? ""; }
}
