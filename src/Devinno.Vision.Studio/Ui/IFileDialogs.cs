namespace Devinno.Vision.Studio.Ui;

/// <summary>파일 · 폴더 고르기. 화면은 이 인터페이스만 알고, WinForms 구현은 Program.cs 에 있다 (테스트는 가짜).</summary>
public interface IFileDialogs
{
    /// <summary>이미지 파일 하나를 고른다. 취소하면 null.</summary>
    string? PickImage();

    /// <summary>폴더 하나를 고른다. 취소하면 null.</summary>
    string? PickFolder();

    /// <summary>ONNX 모델 파일 하나를 고른다. 취소하면 null (기본: 고르기 없음).</summary>
    string? PickModel() => null;
}
