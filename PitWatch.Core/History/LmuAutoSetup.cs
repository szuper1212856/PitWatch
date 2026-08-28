using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PitWatch.History;

public class LmuSetupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? PluginFolder { get; set; }
}

/// <summary>
/// Enables the rF2 shared memory plugin for Le Mans Ultimate.
///
/// WHY THIS IS AUTOMATED: unlike rFactor 2, LMU has no in-game plugin toggle. The only way
/// to enable it is by hand-editing CustomPluginVariables.JSON - and the key it looks for is
/// " Enabled" with a LEADING SPACE, which is not a typo and is exactly the sort of thing
/// people get wrong and then can't work out why nothing happens. Doing it in code removes
/// the whole class of problem.
///
/// This only edits the config. The DLL itself still has to be downloaded by the user,
/// because it's someone else's project and bundling it would mean redistributing their
/// binary - so this checks for it and says clearly where to put it if it's missing.
/// </summary>
public static class LmuAutoSetup
{
    private const string PluginFileName = "rFactor2SharedMemoryMapPlugin64.dll";

    /// <summary>Common install locations, checked in order. Steam can be on any drive,
    /// so this also scans other fixed drives before giving up.</summary>
    public static string? FindLmuFolder()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Le Mans Ultimate",
            @"C:\Program Files\Steam\steamapps\common\Le Mans Ultimate",
        };

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            candidates.Add(Path.Combine(drive.Name, @"SteamLibrary\steamapps\common\Le Mans Ultimate"));
            candidates.Add(Path.Combine(drive.Name, @"Steam\steamapps\common\Le Mans Ultimate"));
            candidates.Add(Path.Combine(drive.Name, @"Games\Steam\steamapps\common\Le Mans Ultimate"));
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    public static LmuSetupResult TryEnable(string? lmuFolder = null)
    {
        lmuFolder ??= FindLmuFolder();

        if (lmuFolder == null)
        {
            return new LmuSetupResult
            {
                Success = false,
                Message = "Couldn't find your Le Mans Ultimate folder automatically. In Steam, right-click "
                        + "Le Mans Ultimate, choose Manage then Browse local files, and use Pick folder to point PitWatch at it."
            };
        }

        try
        {
            var pluginFolder = Path.Combine(lmuFolder, "Plugins");
            Directory.CreateDirectory(pluginFolder);

            var dllPath = Path.Combine(pluginFolder, PluginFileName);
            if (!File.Exists(dllPath))
            {
                return new LmuSetupResult
                {
                    Success = false,
                    PluginFolder = pluginFolder,
                    Message = $"Found LMU, but {PluginFileName} isn't in its Plugins folder yet. "
                            + "Download it from the link above, drop it in the folder PitWatch just opened, then click this button again."
                };
            }

            var configPath = Path.Combine(lmuFolder, "UserData", "player", "CustomPluginVariables.JSON");
            if (!File.Exists(configPath))
            {
                return new LmuSetupResult
                {
                    Success = false,
                    PluginFolder = pluginFolder,
                    Message = "The plugin is in place, but LMU hasn't created its plugin settings file yet. "
                            + "Launch Le Mans Ultimate once, close it again, then click this button."
                };
            }

            string raw = File.ReadAllText(configPath);
            JsonObject root;

            // LMU writes literal "null" into this file before it has any plugin entries.
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null")
            {
                root = new JsonObject();
            }
            else
            {
                root = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
            }

            // The key really does have a leading space - that's what the game looks for.
            var entry = root[PluginFileName]?.AsObject() ?? new JsonObject();
            entry[" Enabled"] = 1;
            root[PluginFileName] = entry;

            File.WriteAllText(configPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            return new LmuSetupResult
            {
                Success = true,
                PluginFolder = pluginFolder,
                Message = "Enabled successfully.\n\n"
                        + "What was done: the plugin DLL was found in your Plugins folder, and "
                        + "\" Enabled\" was set to 1 for it in CustomPluginVariables.JSON.\n\n"
                        + "Next: fully close Le Mans Ultimate if it's open, then start it again. "
                        + "PitWatch will show \"Connected - LMU\" once you're on track."
            };
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("LMU plugin setup failed.", ex);
            return new LmuSetupResult
            {
                Success = false,
                Message = $"Something went wrong: {ex.Message}"
            };
        }
    }

    /// <summary>Reports what's currently set up, for showing status without changing anything.</summary>
    public static string DescribeStatus()
    {
        var folder = FindLmuFolder();
        if (folder == null) return "Le Mans Ultimate not found on this PC.";

        var dll = Path.Combine(folder, "Plugins", PluginFileName);
        if (!File.Exists(dll)) return "LMU found, but the plugin DLL isn't installed yet.";

        var configPath = Path.Combine(folder, "UserData", "player", "CustomPluginVariables.JSON");
        if (!File.Exists(configPath)) return "Plugin installed. Launch LMU once so it creates its settings file.";

        try
        {
            var raw = File.ReadAllText(configPath);
            if (raw.Trim() == "null" || string.IsNullOrWhiteSpace(raw))
                return "Plugin installed but not enabled yet.";

            var root = JsonNode.Parse(raw)?.AsObject();
            var enabled = root?[PluginFileName]?[" Enabled"]?.GetValue<int>() ?? 0;
            return enabled == 1
                ? "Plugin installed and enabled."
                : "Plugin installed but not enabled yet.";
        }
        catch
        {
            return "Plugin installed - couldn't read the settings file to confirm it's enabled.";
        }
    }
}
