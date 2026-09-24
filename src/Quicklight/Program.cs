using Quicklight.Mcp;

namespace Quicklight;

/// <summary>One exe, two modes: the launcher (default) and the MCP stdio server (--mcp).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // MCP clients start us with redirected stdin/stdout; a GUI-subsystem exe still inherits those handles.
        if (args.Contains("--mcp")) return McpHost.RunAsync(args).GetAwaiter().GetResult();
        // The one elevated step of the bundled Everything setup (started through UAC by the launcher).
        if (args.Contains("--install-everything")) return Quicklight.Core.Everything.EverythingBootstrap.InstallElevated();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
