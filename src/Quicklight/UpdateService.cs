using System.Windows;
using Quicklight.Core;
using Quicklight.Core.Activity;
using Quicklight.Core.Update;

namespace Quicklight;

/// <summary>
/// Finds, downloads and installs newer releases, both for the "update" command and in the background
/// (<see cref="QuicklightSettings.AutoUpdate"/>): shortly after start and every few hours it checks GitHub, downloads
/// and verifies a newer release, and installs it the next time the launcher is closed.
/// </summary>
public sealed class UpdateService(QuicklightSettings settings)
{
    static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    readonly SemaphoreSlim _downloadGate = new(1, 1);
    (Version Version, string Path)? _ready; // a verified download waiting to be installed
    bool _installing;

    /// <summary>The newer release found by the last check, or null.</summary>
    public ReleaseInfo? Available { get; private set; }

    public static string ExePath => Environment.ProcessPath ?? throw new UpdateException("Cannot tell where Quicklight is installed.");

    /// <summary>Only the published single-file exe updates itself, never a development build.</summary>
    static bool IsPublishedBuild => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);

    /// <summary>Asks GitHub for the latest release; returns it (even when not newer), or null when there is none.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        using var updater = new Updater(settings.UpdateRepository);
        var latest = await updater.GetLatestAsync(ct);
        Available = latest is not null && latest.Version > Updater.CurrentVersion ? latest : null;
        return latest;
    }

    /// <summary>
    /// Downloads and verifies <paramref name="release"/>, shown as a progress bar. A download the background check
    /// already finished is reused, and a running one is waited for rather than started twice.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, string? detail, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct);
        try
        {
            if (_ready is { } ready && ready.Version == release.Version && File.Exists(ready.Path))
            {
                progress?.Report(1);
                return ready.Path;
            }
            var title = $"Quicklight v{release.Version.ToString(3)} 내려받는 중";
            using var activity = ActivityTracker.Shared.Begin("update", title, 0, detail);
            using var updater = new Updater(settings.UpdateRepository);
            var path = await updater.DownloadAsync(release, ExePath, Updater.CurrentVersion, new Progress<double>(p =>
            {
                activity.Report(title, p, detail);
                progress?.Report(p);
            }), ct);
            _ready = (release.Version, path);
            return path;
        }
        finally { _downloadGate.Release(); }
    }

    /// <summary>Swaps in the verified download, starts it and exits; the new version removes the old file.</summary>
    public void Install(string downloaded)
    {
        if (_installing) return;
        _installing = true;
        try { Updater.ApplyAndRestart(downloaded, ExePath); }
        catch
        {
            _installing = false;
            _ready = null;
            throw;
        }
        ((App)Application.Current).ShutdownForUpdate();
    }

    /// <summary>Runs the background check loop on the UI thread. <paramref name="launcherOpen"/> defers the restart.</summary>
    public async Task RunBackgroundAsync(Func<bool> launcherOpen)
    {
        if (!IsPublishedBuild) return;
        await Task.Delay(FirstCheck);
        while (!_installing)
        {
            if (settings.AutoUpdate)
            {
                try
                {
                    await CheckAsync(CancellationToken.None);
                    if (Available is { } release)
                    {
                        Log.Info($"background update: v{release.Version.ToString(3)} found, downloading");
                        var path = await DownloadAsync(release, null, "자동 업데이트", CancellationToken.None);
                        // Never restart under the user's hands: wait until the launcher is closed.
                        while (launcherOpen()) await Task.Delay(TimeSpan.FromSeconds(3));
                        if (_installing) return;
                        Log.Info($"background update: installing v{release.Version.ToString(3)}");
                        Install(path);
                        return;
                    }
                }
                catch (Exception ex) when (ex is UpdateException or System.Net.Http.HttpRequestException or IOException
                                               or UnauthorizedAccessException or OperationCanceledException
                                               or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
                {
                    Log.Error("background update failed", ex);
                }
            }
            await Task.Delay(Interval);
        }
    }
}
