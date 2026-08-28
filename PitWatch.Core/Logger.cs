using System.IO;
using System.Text;

namespace PitWatch;

/// <summary>
/// Writes timestamped log lines to a file next to the executable, so when something goes
/// wrong on someone else's machine there's an actual record to look at instead of just
/// "it didn't work". Rotates once the file gets large so it can't grow without bound.
/// </summary>
public static class Logger
{
    private static readonly object Lock = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB

    public static string LogPath { get; } = UserDataPaths.LogFile;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                // Logging can happen before anything else has created the folder
                // (including during migration itself), so make sure it exists here.
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RotateIfNeeded();
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the thing that crashes the app - if the log file is
            // locked or the folder is read-only, silently carry on.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes) return;

            string old = LogPath + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(LogPath, old);
        }
        catch { /* rotation is best-effort */ }
    }

    /// <summary>Wipes the log at startup boundaries so each run is easy to read.</summary>
    public static void StartNewRun(string version)
    {
        Write("INFO", new string('=', 60));
        Write("INFO", $"PitWatch {version} starting - {Environment.OSVersion}, .NET {Environment.Version}");
    }
}
