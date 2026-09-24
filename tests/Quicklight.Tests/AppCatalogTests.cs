using Quicklight.Core.Shell;

namespace Quicklight.Tests;

public class AppCatalogTests
{
    [Theory]
    [InlineData("앱", true)]
    [InlineData(" app ", true)]
    [InlineData("Apps", true)]
    [InlineData("application", true)]
    [InlineData("애플리케이션", true)]
    [InlineData("응용프로그램", true)]
    [InlineData("프로그램", true)]
    [InlineData("appdata", false)]
    [InlineData("앱스토어", false)]
    public void Catalog_keywords(string q, bool expected) => Assert.Equal(expected, AppCatalog.IsCatalogQuery(q));

    [Theory]
    [InlineData("chrome", "C")]
    [InlineData("카카오톡", "ㅋ")]
    [InlineData("까치", "ㄱ")]
    [InlineData("7-Zip", "#")]
    [InlineData("  Zoom", "Z")]
    public void Sections(string name, string section) => Assert.Equal(section, AppCatalog.SectionOf(name));

    [Fact]
    public void App_paths_resolve_from_known_folders_and_shortcut_targets()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        Assert.Equal(Path.Combine(x86, @"FinalWire\AIDA64 Extreme\aida64.exe"),
            new AppEntry("AIDA64", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\FinalWire\AIDA64 Extreme\aida64.exe").FilePath);
        Assert.Equal(@"C:\Tools\code.exe", new AppEntry("Code", "Microsoft.VisualStudioCode", @"C:\Tools\code.exe").FilePath);
        Assert.Null(new AppEntry("Docs", "308046B0AF4A39CB", "http://example.com").FilePath);
        Assert.Null(new AppEntry("메모장", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App").FilePath);
    }

    [Fact]
    public void Builds_sorted_clean_sections()
    {
        var apps = new[]
        {
            new AppEntry("카카오톡", "a"), new AppEntry("Zoom", "b"), new AppEntry("chrome", "c"), new AppEntry("7-Zip", "d"),
            new AppEntry("Uninstall Zoom", "e"), new AppEntry("가계부", "f"), new AppEntry("Chrome", "g"), new AppEntry("Code", "h"),
        };
        var sections = AppCatalog.Build(apps);
        Assert.Equal(["C", "Z", "ㄱ", "ㅋ", "#"], sections.Select(s => s.Name));
        Assert.Equal(["chrome", "Code"], sections[0].Apps.Select(a => a.Name)); // duplicate "Chrome" dropped, sorted ignoring case
        Assert.DoesNotContain(sections.SelectMany(s => s.Apps), a => a.Name.StartsWith("Uninstall"));
    }
}
