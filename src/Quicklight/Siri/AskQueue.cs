namespace Quicklight.Siri;

/// <summary>
/// Files handed to the running Quicklight by Explorer's "Quicklight에게 묻기". Explorer starts one process per selected
/// file; each appends its path here and the running instance collects them all after a short pause.
/// </summary>
static class AskQueue
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "ask-queue.txt");

    public static void Enqueue(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { File.AppendAllText(FilePath, path + "\n"); return; }
            catch (IOException) { Thread.Sleep(30); } // another process is appending right now
        }
    }

    /// <summary>Takes everything queued so far (the file moves away first, so a late append starts a new batch).</summary>
    public static List<string> Drain()
    {
        var taken = FilePath + "." + Guid.NewGuid().ToString("N");
        try { File.Move(FilePath, taken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        // A path another process queued must not make us reach out to a server: no network paths.
        try { return File.ReadAllLines(taken).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(@"\\") && File.Exists(l)).Distinct().ToList(); }
        finally { try { File.Delete(taken); } catch (IOException) { } }
    }
}
