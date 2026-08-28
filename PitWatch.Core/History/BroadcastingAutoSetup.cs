using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PitWatch;

namespace PitWatch.History;

public class BroadcastingSetupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Handles the previously-manual "edit two JSON files by hand" setup for ACC's
/// Broadcasting SDK. This reads (or creates) ACC's own broadcasting.json, sets a fixed
/// known port/password if it looks untouched, and syncs the same values into PitWatch's
/// own config - so enabling car proximity/track map becomes a single button press instead
/// of manually editing files in two different folders.
/// </summary>
public static class BroadcastingAutoSetup
{
    private const int DefaultPort = 9000;
    private const string DefaultPassword = "pitwatch";

    private static string AccConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Assetto Corsa Competizione", "Config", "broadcasting.json");

    public class BroadcastingFileStatus
    {
        public bool FileExists { get; set; }
        public bool ParsedOk { get; set; }
        public int Port { get; set; }
        public string Password { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Reads back what ACC's broadcasting.json actually currently contains, so the UI
    /// can show real values instead of asking someone to open and decode a possibly
    /// UTF-16-encoded file by eye.
    /// </summary>
    public static BroadcastingFileStatus ReadCurrentAccConfig()
    {
        string path = AccConfigPath;
        var status = new BroadcastingFileStatus();

        if (!File.Exists(path))
        {
            status.Error = "ACC's broadcasting.json doesn't exist yet - launch ACC at least once first.";
            return status;
        }
        status.FileExists = true;

        try
        {
            var encoding = DetectEncoding(path);
            var json = JsonNode.Parse(File.ReadAllText(path, encoding))?.AsObject();
            if (json == null)
            {
                status.Error = "File exists but couldn't be parsed as JSON.";
                return status;
            }

            status.ParsedOk = true;
            status.Port = json["updListenerPort"]?.GetValue<int>() ?? 0;
            status.Password = json["connectionPassword"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }

        return status;
    }

    public static BroadcastingSetupResult TryEnable(Config config)
    {
        string path = AccConfigPath;

        if (!File.Exists(path))
        {
            return new BroadcastingSetupResult
            {
                Success = false,
                Message = "Couldn't find ACC's settings folder. Make sure you've launched ACC at least once "
                         + "(even just to the main menu) so it creates its config files, then try again."
            };
        }

        try
        {
            Encoding detectedEncoding = DetectEncoding(path);
            string rawText = File.ReadAllText(path, detectedEncoding);
            var json = JsonNode.Parse(rawText)?.AsObject();
            if (json == null)
            {
                return new BroadcastingSetupResult { Success = false, Message = "ACC's broadcasting.json exists but couldn't be read - it may be corrupted." };
            }

            // Only overwrite if it looks like the untouched default (0 or empty) - don't
            // clobber a value the person or another tool already set on purpose.
            int existingPort = json["updListenerPort"]?.GetValue<int>() ?? 0;
            string existingPassword = json["connectionPassword"]?.GetValue<string>() ?? "";

            int portToUse = existingPort > 0 ? existingPort : DefaultPort;
            string passwordToUse = !string.IsNullOrEmpty(existingPassword) ? existingPassword : DefaultPassword;

            json["updListenerPort"] = portToUse;
            json["connectionPassword"] = passwordToUse;
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), detectedEncoding);

            config.BroadcastingEnabled = true;
            config.BroadcastingPort = portToUse;
            config.BroadcastingPassword = passwordToUse;
            config.BroadcastingIp = "127.0.0.1";
            config.Save();

            return new BroadcastingSetupResult
            {
                Success = true,
                Message = "Done! Car proximity callouts and the track map will work next time you're on track."
            };
        }
        catch (Exception ex)
        {
            return new BroadcastingSetupResult { Success = false, Message = $"Something went wrong: {ex.Message}" };
        }
    }

    public static void Disable(Config config)
    {
        config.BroadcastingEnabled = false;
        config.Save();
    }

    /// <summary>
    /// ACC's own config files are sometimes UTF-16 encoded rather than UTF-8/ASCII - a
    /// known quirk that makes a plain File.ReadAllText see embedded null bytes and throw
    /// a confusing "'0x00' is an invalid start of a property name" JSON error. This checks
    /// the BOM first (reliable when present), and falls back to a simple heuristic (lots of
    /// null bytes = UTF-16) when there's no BOM at all.
    /// </summary>
    private static Encoding DetectEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;       // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode; // UTF-16 BE BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8; // UTF-8 BOM

        // No BOM - check if it looks like UTF-16 anyway (lots of null bytes is a strong signal,
        // since plain ASCII/UTF-8 JSON essentially never contains them).
        int nullCount = bytes.Take(Math.Min(bytes.Length, 200)).Count(b => b == 0);
        return nullCount > 20 ? Encoding.Unicode : Encoding.UTF8;
    }
}
