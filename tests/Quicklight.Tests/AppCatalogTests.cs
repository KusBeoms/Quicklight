using Quicklight.Core.Providers;
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
    [InlineData("app apple music", "apple music")]
    [InlineData("app : apple music", "apple music")]
    [InlineData("app ; apple music", "apple music")]
    [InlineData("app, apple music", "apple music")]
    [InlineData("APP:apple music", "apple music")]
    [InlineData("앱 멜론", "멜론")]
    [InlineData("application; code", "code")]
    [InlineData("apple music", null)] // no separator after the keyword
    [InlineData("app", null)]
    [InlineData("app : ", null)]
    [InlineData("앱스토어", null)]
    public void Scoped_app_queries(string q, string? rest) => Assert.Equal(rest, AppCatalog.ScopedQuery(q));

    [Theory]
    [InlineData("chrome", "C")]
    [InlineData("카카오톡", "ㅋ")]
    [InlineData("까치", "ㄱ")]
    [InlineData("7-Zip", "#")]
    [InlineData("  Zoom", "Z")]
    public void Sections(string name, string section) => Assert.Equal(section, AppCatalog.SectionOf(name));

    static ShellApps.Shortcut Link(string name, string? target, bool desktop = false, string? args = null) =>
        new($@"C:\Menu\{name}.lnk", name, target, args, desktop);

    [Fact]
    public void Groups_shortcuts_uninstallers_registry_and_installers_into_one_app()
    {
        const string zoomExe = @"C:\Users\u\AppData\Roaming\Zoom\bin\Zoom.exe";
        var apps = ShellApps.Group(
            [
                Link("Zoom Workplace", zoomExe),
                Link("Zoom", zoomExe, desktop: true), // same program on the desktop
                Link("Uninstall Zoom", @"C:\Users\u\AppData\Roaming\Zoom\uninstall\Installer.exe"),
                Link("Zoom Help", "https://support.zoom.us"),
                Link("Python 3.11", @"C:\Python311\python.exe"),
                Link("Python 3.12", @"C:\Python312\python.exe"), // versions are different apps
                Link("Notes", @"C:\Users\u\Desktop\notes.txt", desktop: true), // desktop documents are not apps
            ],
            [new ShellApps.Program("Zoom Workplace (64-bit)", "Zoom", "6.0.1", null, zoomExe, "\"C:\\Zoom\\uninstall.exe\" /uninstall")],
            [@"C:\Users\u\Downloads\ZoomInstallerFull.exe", @"C:\Users\u\Downloads\setup.exe"],
            [new AppEntry("메모장", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App")]);

        Assert.Equal(["Zoom Workplace", "Python 3.11", "Python 3.12", "메모장"], apps.Select(a => a.Name));
        var zoom = apps[0];
        Assert.Equal([AppPartKind.Shortcut, AppPartKind.Exe, AppPartKind.Shortcut, AppPartKind.Uninstaller, AppPartKind.Uninstaller, AppPartKind.Installer],
            zoom.Parts.Select(p => p.Kind));
        Assert.Equal(("Zoom", "6.0.1"), (zoom.Publisher, zoom.Version));
        Assert.Equal(@"C:\Menu\Zoom Workplace.lnk", zoom.LaunchTarget); // nothing exists on disk: the first by priority
    }

    [Fact]
    public async Task Localized_shortcut_names_keep_the_english_one_as_an_alias()
    {
        var apps = ShellApps.Group([new ShellApps.Shortcut(@"C:\Menu\File Explorer.lnk", "파일 탐색기", @"C:\Windows\explorer.exe", null, false, "File Explorer")], [], [], []);
        var app = Assert.Single(apps);
        Assert.Equal("파일 탐색기", app.Name);
        Assert.Equal(["File Explorer"], app.Aliases);
        var provider = new AppProvider(apps);
        foreach (var q in new[] { "파일탐색기", "file explore", "탐색기" })
            Assert.Equal("파일 탐색기", Assert.Single(await provider.QueryAsync(new QueryContext(q), default)).Title);
    }

    [Fact]
    public void Updater_and_other_tools_join_the_app_in_their_folder()
    {
        const string dir = @"C:\NoSuch\Electronic Arts\EA Desktop"; // not on disk: Enter's choice is then the first by priority
        var apps = ShellApps.Group(
            [
                Link("EA", dir + @"\EALauncher.exe"),
                Link("EA app 업데이터", dir + @"\EAUpdater.exe"),
                Link("EA Updater", dir + @"\EAUpdater.exe"),
                Link("EA Error Reporter", dir + @"\ErrorReporter.exe"),
                Link("App Recovery", dir + @"\EALauncher.exe", args: "-recovery"),
                Link("Some Updater", @"C:\Tools\Updater\update.exe"), // nobody's: an app of its own
            ],
            [new ShellApps.Program("EA app", "Electronic Arts", "13.0", null, null, $"\"{dir}\\EAUninstall.exe\" /uninstall")],
            [], []);

        Assert.Equal(["EA", "Some Updater"], apps.Select(a => a.Name));
        var ea = apps[0];
        Assert.Equal(4, ea.Parts.Count(p => p.Kind == AppPartKind.Tool));
        Assert.Equal("Electronic Arts", ea.Publisher); // the uninstaller sits next to the program
        Assert.Equal(@"C:\Menu\EA.lnk", ea.LaunchTarget); // tools never run on Enter
    }

    [Fact]
    public async Task Chrome_without_a_shortcut_comes_from_the_registry_and_web_apps_stay_apart()
    {
        const string dir = @"C:\NoSuch\Google\Chrome\Application";
        var apps = ShellApps.Group(
            [Link("GitHub", dir + @"\chrome_proxy.exe", args: "--profile-directory=\"Profile 6\" --app-id=abc")],
            [
                new ShellApps.Program("Google Chrome", "Google LLC", "153.0", dir, dir + @"\chrome.exe", $"\"{dir}\\setup.exe\" --uninstall", Runnable: true),
                new ShellApps.Program("GitHub", "Google\\Chrome", "1.0", null, null, $"\"{dir}\\chrome.exe\" --uninstall-app-id=abc"),
            ],
            [], []);

        Assert.Equal(["GitHub", "Google Chrome"], apps.Select(a => a.Name));
        var github = apps[0];
        Assert.Equal("Google\\Chrome", github.Publisher); // its own web-app entry, not Chrome's
        Assert.Equal("GitHub 제거", AppProvider.PartResult(github, github.Part(AppPartKind.Uninstaller)!).Title);

        var results = await new AppProvider(apps).QueryAsync(new QueryContext("chrome"), default);
        Assert.Equal("Google Chrome", Assert.Single(results).Title); // not GitHub through chrome_proxy.exe
    }

    [Fact]
    public void Part_rows_run_the_part_and_the_uninstaller_asks_twice()
    {
        var app = new AppEntry("Zoom", [new AppPart(AppPartKind.Shortcut, @"C:\Menu\Zoom.lnk"), new AppPart(AppPartKind.Uninstaller, "\"C:\\Zoom\\uninstall.exe\" /x")]);
        var shortcut = AppProvider.PartResult(app, app.Parts[0]);
        Assert.Equal((@"C:\Menu\Zoom.lnk", "Zoom", false), (shortcut.Target, shortcut.Title, shortcut.RequiresConfirmation));
        Assert.StartsWith("바로 가기 · 앱의 Enter로 실행", shortcut.Subtitle);
        var uninstall = AppProvider.PartResult(app, app.Parts[1]);
        Assert.Equal((@"C:\Zoom\uninstall.exe", "/x", true), (uninstall.Target, uninstall.Arguments, uninstall.RequiresConfirmation));
    }

    [Fact]
    public void Enter_prefers_shortcut_then_exe_then_installer_that_exists()
    {
        var dir = Directory.CreateTempSubdirectory("ql-app").FullName;
        try
        {
            string exe = Path.Combine(dir, "app.exe"), installer = Path.Combine(dir, "setup.exe"), lnk = Path.Combine(dir, "App.lnk");
            var app = new AppEntry("App", [new AppPart(AppPartKind.Uninstaller, "unins000.exe"), new AppPart(AppPartKind.Installer, installer),
                new AppPart(AppPartKind.Exe, exe, "--flag"), new AppPart(AppPartKind.Shortcut, lnk)]);
            File.WriteAllText(installer, "");
            Assert.Equal(installer, app.LaunchTarget); // only the installer is left
            File.WriteAllText(exe, "");
            Assert.Equal(new AppPart(AppPartKind.Exe, exe, "--flag"), app.Launch);
            File.WriteAllText(lnk, "");
            Assert.Equal(AppPartKind.Shortcut, app.Launch.Kind);
            Assert.Equal(lnk, app.IconSource); // the icon follows what Enter runs, not the (often generic) program
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Database_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ql-apps-{Guid.NewGuid():N}.db");
        try
        {
            var app = new AppEntry("Zoom", [new AppPart(AppPartKind.Shortcut, @"C:\a.lnk"), new AppPart(AppPartKind.Exe, @"C:\z.exe", "-x")]) { Publisher = "Zoom" };
            app.AddAlias("Zoom Workplace");
            AppDatabase.Save(path, [app, new AppEntry("메모장", "Notepad!App")]);
            AppDatabase.Save(path, [app]); // a rescan replaces the list
            var loaded = Assert.Single(AppDatabase.Load(path));
            Assert.Equal("Zoom", loaded.Name);
            Assert.Equal("Zoom", loaded.Publisher);
            Assert.Equal(app.Parts, loaded.Parts);
            Assert.Equal(["Zoom Workplace"], loaded.Aliases);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", @"C:\Program Files\App\unins000.exe", "/SILENT")]
    [InlineData("MsiExec.exe /X{1234}", "MsiExec.exe", "/X{1234}")]
    [InlineData(@"C:\Tools\uninstall.exe", @"C:\Tools\uninstall.exe", "")]
    public void Splits_uninstall_command_lines(string line, string file, string args) =>
        Assert.Equal((file, args), ShellLauncher.SplitCommandLine(line));

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
