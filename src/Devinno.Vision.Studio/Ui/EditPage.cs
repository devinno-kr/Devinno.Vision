using Devinno.UI.App;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Ui;

/// <summary>편집 화면 — 기준 이미지 · ROI · 검사별 속성 (내용은 Task 8).</summary>
public sealed partial class EditPage : DvPage
{
    private readonly StudioSession _session;

    public EditPage(StudioSession session) => _session = session;

    public string EmptyText => "기준 이미지를 여세요";
}
