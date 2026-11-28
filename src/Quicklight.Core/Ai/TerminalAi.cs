namespace Quicklight.Core.Ai;

/// <summary>Prompts and answer cleanup for the terminal helper ("? 요청" makes a command, "??" explains the last error).</summary>
public static class TerminalAi
{
    // The prompts are Prompts.TerminalCommand and Prompts.TerminalExplain.
    public static string CommandUser(string dir, string request) => $"OS: Windows (cmd.exe)\nCurrent directory: {dir}\nRequest: {request}";

    /// <summary>The command with the last 60 lines / 4000 chars of its output.</summary>
    public static string ExplainUser(string command, string output)
    {
        var tail = string.Join('\n', output.Replace("\r", "").Split('\n').TakeLast(60));
        if (tail.Length > 4000) tail = tail[^4000..];
        return $"명령: {command}\n출력:\n{tail}";
    }

    /// <summary>First line only, without fences, backticks or a leading "$ " / "&gt; ".</summary>
    public static string CleanCommand(string answer)
    {
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```")) continue;
            line = line.Trim('`').Trim();
            if (line.StartsWith("$ ") || line.StartsWith("> ")) line = line[2..].Trim();
            return line;
        }
        return "";
    }

    /// <summary>Non-zero exit code, or obvious error text in the output.</summary>
    public static bool LooksFailed(int exitCode, string output) =>
        exitCode != 0 || output.Contains("is not recognized as an internal or external command") || output.Contains("내부 또는 외부 명령");
}
