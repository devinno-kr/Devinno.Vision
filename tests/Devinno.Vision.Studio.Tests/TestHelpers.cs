using Devinno.UI;
using Devinno.Vision.Studio.Ui;

namespace Devinno.Vision.Studio.Tests;

// 파일 · 폴더 고르기 없음 (테스트용)
internal sealed class NullDialogs : IFileDialogs
{
    public string? PickImage() => null;
    public string? PickFolder() => null;
}

internal static class Png
{
    // 앱 화면 한 장 (DvRenderer 는 surface 를 파일로만 내보낸다)
    public static byte[] Render(DvSurface surface, int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.png");
        try
        {
            DvRenderer.RenderToFile(surface, width, height, path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

// 테스트마다 지우는 임시 폴더
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "studio-test-" + Guid.NewGuid().ToString("N")[..8]);

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class Samples
{
    public static string BracketDir => Path.Combine(AppContext.BaseDirectory, "Samples", "Bracket");

    public static string Bracket(string name) => Path.Combine(BracketDir, name);
}
