using System.IO;
using System.Text.Json;

namespace PitWatch;

public class Config
{
    public string GeminiApiKey { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-flash-latest";
    public string PushToTalkKey { get; set; } = "RControlKey";
    public string PreferredGame { get; set; } = "Auto";
    public int SpeechVoiceRate { get; set; } = 0;
    public int SpeechVoiceVolume { get; set; } = 100;
    public bool BroadcastingEnabled { get; set; } = false;
    public string BroadcastingIp { get; set; } = "127.0.0.1";
    public int BroadcastingPort { get; set; } = 9000;
    public string BroadcastingPassword { get; set; } = "";
    public string BroadcastingCommandPassword { get; set; } = "";
    public string ThemeMode { get; set; } = "Dark"; // "Dark" or "Light"
    public bool ColorblindMode { get; set; } = false;

    // v1.0 additions
    public string Personality { get; set; } = "Helpful"; // Helpful, Kind, Mean, Professional
    public string Chattiness { get; set; } = "Normal";   // Quiet, Normal, Chatty
    public string AccentColor { get; set; } = "Green";
    public bool RadioBeepEnabled { get; set; } = true;
    public bool ShowSpeedTrace { get; set; } = true;
    public bool ShowPedalTrace { get; set; } = true;
    public bool ShowGForce { get; set; } = true;
    public bool AnnounceOvertakes { get; set; } = true;
    public bool AnnounceLapAnalysis { get; set; } = true;
    public bool AnnounceProximity { get; set; } = true;
    public bool VoiceInputEnabled { get; set; } = true;
    public string VoiceInputBinding { get; set; } = ""; // e.g. "joy0:5" for a wheel button
    public bool AnnounceTyreTemps { get; set; } = true;
    public bool AnnounceStintSummary { get; set; } = true;
    public bool UseElevenLabs { get; set; } = false;
    public string ElevenLabsApiKey { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM"; // ElevenLabs default demo voice ("Rachel")
    public bool SetupCompleted { get; set; } = false; // lets Setup be skippable without re-triggering every launch
    public bool DeveloperMode { get; set; } = false;

    public bool HasGeminiKey => !string.IsNullOrWhiteSpace(GeminiApiKey) && !GeminiApiKey.Contains("PASTE_YOUR");
    public bool HasElevenLabsKey => !string.IsNullOrWhiteSpace(ElevenLabsApiKey);

    /// <summary>
    /// Resolves config.json next to the executable rather than the current working
    /// directory - launching from a shortcut or a different folder would otherwise silently
    /// create a second config and appear to "forget" all settings.
    /// </summary>
    public static string DefaultPath => UserDataPaths.ConfigFile;

    public static Config Load(string? path = null)
    {
        path ??= DefaultPath;

        if (!File.Exists(path))
        {
            Logger.Info($"No config found at {path}, starting with defaults.");
            return new Config();
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<Config>(json) ?? new Config();

            // Keys are stored encrypted; decrypt into memory for use.
            cfg.GeminiApiKey = SecureStore.Unprotect(cfg.GeminiApiKey);
            cfg.ElevenLabsApiKey = SecureStore.Unprotect(cfg.ElevenLabsApiKey);
            return cfg;
        }
        catch (Exception ex)
        {
            // A corrupt or hand-edited config shouldn't stop the app from starting -
            // fall back to defaults and keep the bad file around for inspection.
            Logger.Error("config.json couldn't be read, using defaults instead.", ex);
            try
            {
                var backup = path + ".broken";
                File.Copy(path, backup, overwrite: true);
                Logger.Info($"Kept a copy of the unreadable config at {backup}");
            }
            catch { /* best effort */ }

            return new Config();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            // Encrypt keys for storage without disturbing the in-memory values, which the
            // running app still needs in plain form.
            var toSave = (Config)MemberwiseClone();
            toSave.GeminiApiKey = SecureStore.Protect(GeminiApiKey);
            toSave.ElevenLabsApiKey = SecureStore.Protect(ElevenLabsApiKey);

            var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });

            // Write to a temp file then swap, so an interrupted save can't leave behind a
            // half-written config that fails to parse next launch.
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't save settings.", ex);
        }
    }
}
