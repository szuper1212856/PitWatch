using Velopack;
using Velopack.Sources;

namespace PitWatch.Gui;

public record UpdateCheckResult(bool UpdateAvailable, string? NewVersion, string? ReleaseNotes);

/// <summary>
/// Checks for and applies updates via Velopack, pulling releases straight from the
/// project's GitHub Releases page.
///
/// Velopack downloads only what changed between versions (delta packages) rather than the
/// whole app each time, and restarts into the new version itself - so users never have to
/// go back to a download page after their first install.
///
/// Every method here fails soft: no internet, GitHub down, or running from a plain
/// unpacked folder rather than an installed copy all just mean "no update right now"
/// instead of an error the user has to deal with mid-race.
/// </summary>
public class UpdateService
{
    // Point this at the real repository before the first public release.
    private const string ReleaseUrl = "https://github.com/szuper1212856/PitWatch";

    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public bool IsSupported { get; private set; }

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(ReleaseUrl, null, false));
            IsSupported = _manager.IsInstalled;
        }
        catch (Exception ex)
        {
            // Running from a dev build or an unpacked zip - updating simply doesn't apply.
            PitWatch.Logger.Info($"Updates not available in this install type: {ex.Message}");
            IsSupported = false;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsSupported || _manager == null) return new UpdateCheckResult(false, null, null);

        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            if (_pending == null) return new UpdateCheckResult(false, null, null);

            string version = _pending.TargetFullRelease.Version.ToString();

            // Release notes come from whatever was passed to vpk pack at build time. They
            // may be absent (older releases, or a build that didn't include them), so this
            // is treated as optional rather than assumed present.
            string? notes = null;
            try
            {
                notes = _pending.TargetFullRelease.NotesMarkdown;
                if (string.IsNullOrWhiteSpace(notes)) notes = null;
            }
            catch (Exception ex)
            {
                PitWatch.Logger.Warn($"Couldn't read release notes: {ex.Message}");
            }
            PitWatch.Logger.Info($"Update available: {version}");
            return new UpdateCheckResult(true, version, notes);
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Warn($"Update check failed (probably offline): {ex.Message}");
            return new UpdateCheckResult(false, null, null);
        }
    }

    /// <summary>Downloads the pending update in the background. Safe to call mid-session -
    /// nothing is swapped until the app actually restarts.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (_manager == null || _pending == null) return false;

        try
        {
            await _manager.DownloadUpdatesAsync(_pending);
            PitWatch.Logger.Info("Update downloaded and ready to apply.");
            return true;
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't download the update.", ex);
            return false;
        }
    }

    /// <summary>Applies the downloaded update and restarts into the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_manager == null || _pending == null) return;

        try
        {
            _manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't apply the update.", ex);
        }
    }
}
