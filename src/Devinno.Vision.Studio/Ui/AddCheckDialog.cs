using Devinno.UI.App;

namespace Devinno.Vision.Studio.Ui;

/// <summary>검사 추가 대화상자의 결과.</summary>
public sealed record AddCheckRequest(string Kind, string Name);

/// <summary>검사 추가 — 이름 + 종류.</summary>
public sealed partial class AddCheckDialog : DvDialog<AddCheckRequest>
{
    public string CheckName { get; set; } = "";

    public string Kind { get; set; } = "BlobCount";

    public bool CanAdd => !string.IsNullOrWhiteSpace(CheckName);

    public bool CannotAdd => !CanAdd;

    public void Ok()
    {
        if (CanAdd)
            Close(new AddCheckRequest(Kind, CheckName.Trim()));
    }

    public void Cancel() => Close();
}
