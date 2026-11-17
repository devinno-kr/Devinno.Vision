using OpenCvSharp;

namespace Devinno.Vision;

// 이미지 파일 읽기 · 쓰기. OpenCV 의 imread / imwrite 는 경로를 PC 의 ANSI 코드 페이지로 넘겨서,
// 그 코드 페이지에 없는 글자(영어 Windows 의 한글 폴더 등)가 있으면 실패한다 → 파일은 .NET 으로 읽고 쓰고, OpenCV 는 메모리에서만 풀고 묶는다.
internal static class ImageFile
{
    // 파일을 읽어 푼다. 없거나 읽을 수 없거나 이미지가 아니면 빈 Mat
    public static Mat Read(string path, ImreadModes mode)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Mat();
        }
        return bytes.Length == 0 ? new Mat() : Cv2.ImDecode(bytes, mode);
    }

    // 확장자에 맞는 형식으로 묶어 쓴다. 형식을 모르는 확장자이거나 파일을 쓸 수 없으면 IOException
    public static void Write(string path, Mat mat)
    {
        var ext = Path.GetExtension(path);
        byte[]? bytes = null;
        try
        {
            if (ext.Length > 1 && ext.All(char.IsAscii) && !Cv2.ImEncode(ext, mat, out bytes))
                bytes = null;
        }
        catch (OpenCVException)
        {
            bytes = null;   // 모르는 확장자
        }
        if (bytes is null)
            throw new IOException($"Cannot write the image file (unknown format '{ext}'): {path}");
        try
        {
            File.WriteAllBytes(path, bytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Cannot write the image file: {path}", ex);
        }
    }
}
