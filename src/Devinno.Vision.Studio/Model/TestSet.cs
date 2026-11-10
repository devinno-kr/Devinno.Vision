namespace Devinno.Vision.Studio.Model;

/// <summary>테스트 이미지의 라벨을 읽는 규칙.</summary>
public enum LabelRule
{
    /// <summary>하위 폴더 이름이 라벨 (MVTec 형식: good/, crack/ …).</summary>
    Folder,

    /// <summary>파일 이름의 첫 '_' 뒤 토큰이 라벨 (02_missinghole.bmp → missinghole).</summary>
    FileName,
}

/// <summary>테스트 폴더 하나.</summary>
public sealed class TestSet
{
    public string Folder { get; set; } = "";
    public LabelRule Rule { get; set; }

    /// <summary>이 라벨이면 OK 를 기대하고, 아니면 NG 를 기대한다.</summary>
    public string GoodLabel { get; set; } = "good";
}
