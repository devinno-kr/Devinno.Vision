using System.Runtime.CompilerServices;
using Devinno.Vision.Tools;

// English is the original (source comments); Korean is the translation (i18n/ko/*.xml, packed to lib/<tfm>/ko/).
// This test fails when an English comment changed and its Korean translation was not updated, so a stale translation
// can't be published. Fix: tools/XmlDocI18n (pending → translate → apply). Same scheme as Devinno.UI.
public class XmlDocI18nTests
{
    private static string RepoRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    // English XML from the latest build of that project (Debug or Release)
    private static string? EnglishXml(string project)
    {
        var bin = Path.Combine(RepoRoot(), "src", project, "bin");
        return Directory.Exists(bin)
            ? Directory.GetFiles(bin, project + ".xml", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    [Theory]
    [InlineData("Devinno.Vision")]
    [InlineData("Devinno.Vision.UI")]
    [InlineData("Devinno.Vision.FlashCap")]
    public void Korean_IntelliSense_matches_the_English_comments(string project)
    {
        var store = Path.Combine(RepoRoot(), "i18n", "ko", project + ".xml");
        Assert.True(File.Exists(store), $"missing {store}");
        var en = EnglishXml(project);
        Assert.True(en is not null, $"{project}.xml not built — build src/{project} first");
        var problems = XmlDocI18n.Check(XmlDocI18n.Load(en!), XmlDocI18n.Load(store));
        Assert.True(problems.Count == 0,
            $"{problems.Count} member(s) out of sync in i18n/ko/{project}.xml:\n" + string.Join("\n", problems.Take(30).Select(p => p.ToString())));
    }
}
