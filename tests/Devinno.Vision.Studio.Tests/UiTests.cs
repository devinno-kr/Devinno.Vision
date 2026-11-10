using Devinno.UI;
using Devinno.Vision.Studio.Session;

namespace Devinno.Vision.Studio.Tests;

public class UiTests
{
    [Fact]
    public void App_renders_empty_edit_page()
    {
        var errors = new List<DvHandlerErrorEventArgs>();
        var app = StudioApp.Create(new StudioSession(), new NullDialogs());
        app.HandlerError += (_, e) => errors.Add(e);

        var png = Png.Render(app.Surface, 1280, 800);

        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "edit-empty.png"), png);   // 눈으로 확인용
        Assert.True(png.Length > 1000);
        Assert.Empty(errors);
    }
}
