using Devinno.UI;
using Devinno.UI.Components;
using Devinno.UI.Styling;
using Devinno.Vision.UI;

namespace Devinno.Vision.Studio.Session;

/// <summary>실행 결과 → PNG (VisionView 를 창 없이 그린다). 화면과 MCP 가 같은 그림을 본다.</summary>
internal static class ResultRenderer
{
    private static readonly ComponentRegistry Registry = CreateRegistry();

    public static byte[] Render(VisionResult r, int width = 960, int height = 720)
    {
        ArgumentNullException.ThrowIfNull(r);
        var view = new DvVisionView();
        try
        {
            view.Show(r);
            var markup = $"<VisionView source=\"{{View}}\" style=\"width:{width}px;height:{height}px\"/>";
            return DvRenderer.RenderToPng(markup, new Vm(view), width, height, DvTheme.Dark, Registry);
        }
        finally
        {
            view.Clear();   // 스냅샷 그림 해제
        }
    }

    private static ComponentRegistry CreateRegistry()
    {
        var r = new ComponentRegistry();
        DvVisionView.Register(r);
        return r;
    }

    public sealed class Vm(DvVisionView view)
    {
        public DvVisionView View { get; } = view;
    }
}
