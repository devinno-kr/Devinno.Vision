namespace Devinno.Vision.Recipes;

/// <summary>How the label of a test image is read.</summary>
public enum LabelRule
{
    /// <summary>The subfolder name is the label (MVTec style: good/, crack/ …).</summary>
    Folder,

    /// <summary>The token after the first '_' in the file name is the label (02_missinghole.bmp → missinghole).</summary>
    FileName,
}

/// <summary>One folder of labelled test images, kept in the recipe file. Studio runs it; running a job does not use it.</summary>
public sealed class TestSet
{
    /// <summary>The folder with the test images.</summary>
    public string Folder { get; set; } = "";

    /// <summary>How labels are read.</summary>
    public LabelRule Rule { get; set; }

    /// <summary>Images with this label are expected OK; all others are expected NG.</summary>
    public string GoodLabel { get; set; } = "good";
}
