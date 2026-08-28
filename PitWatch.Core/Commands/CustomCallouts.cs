using System.IO;
using System.Linq;
using System.Text.Json;

namespace PitWatch.Commands;

/// <summary>
/// Lets users replace any of the built-in spoken lines with their own, via a plain JSON
/// file next to the exe. Each event maps to a list of phrases; one is picked at random,
/// so several variations can be supplied for the same event.
///
/// Falls back to the built-in personality lines whenever an event has no custom entry,
/// so a partially-filled file works fine and a broken file never means silence.
/// </summary>
public class CustomCallouts
{
    private const string FileName = "callouts.json";
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng = new();

    public static readonly string[] KnownEvents =
    {
        "overtake", "overtaken", "green_light", "low_fuel", "damage",
        "race_win", "race_podium", "race_finish", "best_lap", "best_sector",
        "car_left", "car_right", "car_ahead", "car_behind"
    };

    public bool Loaded { get; private set; }

    public void Load()
    {
        try
        {
            string path = UserDataPaths.CalloutsFile;
            if (!File.Exists(path))
            {
                WriteTemplate(path);
                return;
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (parsed == null) return;

            _lines.Clear();
            foreach (var kv in parsed)
            {
                var usable = kv.Value?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (usable is { Count: > 0 }) _lines[kv.Key] = usable;
            }
            Loaded = _lines.Count > 0;
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Warn($"Couldn't read {FileName}: {ex.Message} - using built-in lines.");
        }
    }

    /// <summary>
    /// Reads all configured lines for the settings editor. Returns an empty dictionary if
    /// nothing has been customised, so the editor can show blank boxes rather than failing.
    /// </summary>
    public static Dictionary<string, List<string>> ReadAll()
    {
        try
        {
            string path = UserDataPaths.CalloutsFile;
            if (!File.Exists(path)) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
            return parsed != null
                ? new Dictionary<string, List<string>>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Warn($"Couldn't read {FileName} for editing: {ex.Message}");
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Saves lines from the settings editor. Empty entries are dropped so an
    /// event with no custom lines falls back to the built-in ones.</summary>
    public static bool WriteAll(Dictionary<string, List<string>> lines)
    {
        try
        {
            var cleaned = lines
                .Where(kv => kv.Value != null && kv.Value.Any(v => !string.IsNullOrWhiteSpace(v)))
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList());

            UserDataPaths.EnsureCreated();
            File.WriteAllText(UserDataPaths.CalloutsFile,
                JsonSerializer.Serialize(cleaned, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            PitWatch.Logger.Error("Couldn't save custom callouts.", ex);
            return false;
        }
    }

    /// <summary>Human-readable description of each event, for the settings editor.</summary>
    public static string DescribeEvent(string key) => key switch
    {
        "overtake" => "When you overtake someone",
        "overtaken" => "When someone overtakes you",
        "green_light" => "At the race start",
        "low_fuel" => "When fuel gets critical",
        "damage" => "When you pick up damage",
        "race_win" => "When you win",
        "race_podium" => "When you finish 2nd or 3rd",
        "race_finish" => "When you finish outside the podium",
        "best_lap" => "When you set a personal best lap",
        "best_sector" => "When you set a personal best sector",
        "car_left" => "Car alongside on your left",
        "car_right" => "Car alongside on your right",
        "car_ahead" => "Car close ahead",
        "car_behind" => "Car right behind you",
        _ => key,
    };

    /// <summary>Returns a custom line for this event, or null to use the built-in one.</summary>
    public string? Get(string eventKey)
    {
        if (!_lines.TryGetValue(eventKey, out var options) || options.Count == 0) return null;
        return options[_rng.Next(options.Count)];
    }

    /// <summary>Writes a commented starter file so users have something to edit rather
    /// than a blank page and guesswork about which event names are valid.</summary>
    private static void WriteTemplate(string path)
    {
        try
        {
            var template = new Dictionary<string, List<string>>
            {
                ["_readme"] = new()
                {
                    "Replace any of these with your own lines. Delete an entry to use PitWatch's built-in ones.",
                    "Valid events: " + string.Join(", ", KnownEvents)
                },
                ["overtake"] = new() { "Get in! Great move." },
                ["overtaken"] = new() { "You just lost a spot - respond." },
                ["green_light"] = new() { "GREEN GREEN GREEN, GO!" },
                ["best_lap"] = new() { "That's a new personal best!" },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* template is a convenience, not worth failing startup over */ }
    }
}
