namespace Devinno.Vision;

// VisionSetup.Copy 동안 JSON 에 담기지 않는 무거운 객체(기준 이미지 · 차분 모델)를 같은 순서로 쓰고 읽어 공유한다
internal static class CopyShare
{
    [ThreadStatic] private static List<object?>? s_written;
    [ThreadStatic] private static int s_read;

    public static bool Active => s_written is not null;

    public static void Begin()
    {
        s_written = [];
        s_read = 0;
    }

    public static void End()
    {
        s_written = null;
        s_read = 0;
    }

    public static void Wrote(object? value) => s_written?.Add(value);

    public static T? Next<T>() where T : class
        => s_written is { } w && s_read < w.Count ? w[s_read++] as T : null;
}
