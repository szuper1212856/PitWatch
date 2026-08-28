using System.Linq;
using System.Runtime.InteropServices;
using PitWatch;
using PitWatch.AI;
using PitWatch.Commands;
using PitWatch.Models;
using PitWatch.Telemetry;
using PitWatch.Voice;

// Match the GUI: invariant number formatting regardless of Windows region settings,
// so "12.5 liters" never becomes "12,5 liters" on non-English machines.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

Logger.StartNewRun(AppInfo.Version);
Console.WriteLine($"PitWatch {AppInfo.Version} - AI race engineer starting up...");

var config = Config.Load();
var gemini = new GeminiClient(config.GeminiApiKey, config.GeminiModel, config.Personality);
using var speechOut = new SpeechOutput(config.SpeechVoiceRate, config.SpeechVoiceVolume,
    config.UseElevenLabs, config.ElevenLabsApiKey, config.ElevenLabsVoiceId);
var fuelTracker = new FuelTracker();
var eventWatcher = new EventWatcher();
var idleChatter = new IdleChatter();
var lapAnalyzer = new LapAnalyzer();
var raceRules = new RaceRules();
var stintTracker = new StintTracker();
var customCallouts = new CustomCallouts();
customCallouts.Load();

var accReader = new AccTelemetryReader();
var lmuReader = new LmuTelemetryReader();

int hotkeyVk = MapKeyName(config.PushToTalkKey);
Console.WriteLine($"Push-to-talk key: {config.PushToTalkKey} (VK {hotkeyVk})");
Console.WriteLine($"Personality: {config.Personality} | Chattiness: {config.Chattiness} | Voice: {(config.UseElevenLabs ? "ElevenLabs" : "Windows default")}");
Console.WriteLine("Waiting for ACC or LMU to start... (hold your PTT key, then type your question and press Enter)");

bool keyWasDown = false;

while (true)
{
    ITelemetryProvider? active = null;
    if (accReader.IsAvailable()) active = accReader;
    else if (lmuReader.IsAvailable()) active = lmuReader;

    GameState state = active?.ReadState() ?? new GameState { IsGameRunning = false };
    if (state.IsGameRunning) fuelTracker.Update(state);
    lapAnalyzer.Update(state);
    stintTracker.Update(state);
    eventWatcher.Update(state, speechOut, config, lapAnalyzer, stintTracker, customCallouts);
    idleChatter.MaybeChat(state, speechOut, config);

    bool keyIsDown = (GetAsyncKeyState(hotkeyVk) & 0x8000) != 0;

    if (keyIsDown && !keyWasDown)
    {
        // Key just pressed - prompt for typed input instead of unreliable speech-to-text.
        // Voice OUTPUT (the spoken answer) still works exactly as before.
        Console.Write("Type your question: ");
        string question = Console.ReadLine() ?? "";

        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("[Nothing typed]");
        }
        else if (question.Equals("talk less", StringComparison.OrdinalIgnoreCase))
        {
            idleChatter.SetRuntimeVerbosity(2.5f); // longer gaps between chatter
            Console.WriteLine("[Talking less from now on]");
        }
        else if (question.Equals("talk more", StringComparison.OrdinalIgnoreCase))
        {
            idleChatter.SetRuntimeVerbosity(0.3f); // shorter gaps between chatter
            Console.WriteLine("[Talking more from now on]");
        }
        else if (question.StartsWith("rawg ", StringComparison.OrdinalIgnoreCase))
        {
            // Diagnostic only - dumps ints from the GRAPHICS page (not physics), where
            // flag/session data lives. Usage: "rawg 1150 40"
            var parts = question.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[1], out int offset) && int.TryParse(parts[2], out int count))
            {
                var ints = accReader.DumpRawGraphicsInts(offset, count);
                Console.WriteLine($"[Raw graphics ints @ offset {offset}]: {string.Join(", ", ints)}");
            }
            else
            {
                Console.WriteLine("Usage: rawg <byteOffset> <count>  e.g. rawg 1150 40");
            }
        }
        else if (question.StartsWith("raw ", StringComparison.OrdinalIgnoreCase))
        {
            // Diagnostic only - prints to console, not spoken. Usage: "raw 96 20"
            // dumps 20 floats starting at byte offset 96 in the physics shared memory.
            var parts = question.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[1], out int offset) && int.TryParse(parts[2], out int count))
            {
                var floats = accReader.DumpRawFloats(offset, count);
                Console.WriteLine($"[Raw floats @ offset {offset}]: {string.Join(", ", floats.Select(f => f.ToString("F4")))}");
            }
            else
            {
                Console.WriteLine("Usage: raw <byteOffset> <count>  e.g. raw 96 20");
            }
        }
        else
        {
            string answer = PresetCommands.TryHandle(question, state, lapAnalyzer, raceRules)
                             ?? await gemini.AskAsync(question, state.ToPromptContext());

            Console.WriteLine($"[Answer]: {answer}");
            speechOut.Speak(answer);
        }
    }

    keyWasDown = keyIsDown;
    Thread.Sleep(50);
}

static int MapKeyName(string name) => name switch
{
    "RControlKey" => 0xA3,
    "LControlKey" => 0xA2,
    "RShiftKey" => 0xA1,
    "LShiftKey" => 0xA0,
    "Space" => 0x20,
    _ => 0xA3 // default to Right Ctrl
};

partial class Program
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}
