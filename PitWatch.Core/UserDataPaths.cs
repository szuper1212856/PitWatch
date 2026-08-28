using System.IO;

namespace PitWatch;

/// <summary>
/// Decides where user data lives.
///
/// WHY THIS EXISTS: settings, saved sessions, custom callouts and logs used to sit next to
/// the .exe. That's fine until the app updates itself - an updater replaces the program
/// folder, so everything the user had would vanish with it: their API key, every recorded
/// session, their custom callouts. Keeping user data in %APPDATA%\PitWatch means updates
/// (and reinstalls, and moving the folder) leave it untouched.
///
/// Anything from an older install found next to the .exe is migrated across automatically
/// on first run, so existing users don't silently lose what they already had.
/// </summary>
public static class UserDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PitWatch");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string CalloutsFile => Path.Combine(Root, "callouts.json");
    public static string SessionsFolder => Path.Combine(Root, "Sessions");
    public static string LogFile => Path.Combine(Root, "pitwatch.log");

    private static readonly string LegacyRoot = AppContext.BaseDirectory;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SessionsFolder);
    }

    /// <summary>
    /// Moves data from the old next-to-the-exe location into %APPDATA% if it's still there.
    /// Copies rather than moves, so a failure part-way through can't destroy the originals.
    /// Safe to call on every startup - it no-ops once migration has happened.
    /// </summary>
    public static void MigrateFromLegacyIfNeeded()
    {
        try
        {
            EnsureCreated();

            MigrateFile(Path.Combine(LegacyRoot, "config.json"), ConfigFile);
            MigrateFile(Path.Combine(LegacyRoot, "callouts.json"), CalloutsFile);

            var legacySessions = Path.Combine(LegacyRoot, "Sessions");
            if (Directory.Exists(legacySessions))
            {
                foreach (var file in Directory.GetFiles(legacySessions, "*.json"))
                {
                    MigrateFile(file, Path.Combine(SessionsFolder, Path.GetFileName(file)));
                }
            }
        }
        catch (Exception ex)
        {
            // Migration failing shouldn't stop the app starting - worst case the user
            // starts fresh, which is recoverable. Losing the app entirely is not.
            Logger.Error("Couldn't migrate existing data to the AppData folder.", ex);
        }
    }

    private static void MigrateFile(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        File.Copy(from, to);
        Logger.Info($"Migrated {Path.GetFileName(from)} to {to}");
    }
}
