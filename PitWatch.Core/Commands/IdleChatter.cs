using PitWatch.Models;
using PitWatch.Voice;

namespace PitWatch.Commands;

/// <summary>
/// Occasionally speaks up on its own, unprompted - the way a real race engineer
/// chats with a driver between the actual strategy calls, not just event reactions.
/// Fires at a random interval (2-5 minutes) rather than a fixed schedule.
///
/// COORDINATION: checks SpeechOutput.LastSpokenAt before firing, and stays quiet for a
/// while after anything else was said (event callout, AI answer). This is what was
/// causing "you've lost positions" immediately followed by "you're on your best pace" -
/// two independent systems with no idea what the other just said a few seconds ago.
/// </summary>
public class IdleChatter
{
    private readonly Random _rng = new();
    private DateTime _nextChatterTime = DateTime.UtcNow.AddMinutes(1.5);

    // Runtime override from the "talk more"/"talk less" command - null means use the
    // config-driven default (Quiet/Normal/Chatty) instead.
    private float? _runtimeMultiplier = null;

    public void SetRuntimeVerbosity(float multiplier) => _runtimeMultiplier = multiplier;

    // Don't chatter within this long of any other speech (event callout, AI answer, etc.)
    private static readonly TimeSpan QuietPeriodAfterOtherSpeech = TimeSpan.FromSeconds(25);

    // Pace-aware lines - only used when we actually have valid data for them
    private static readonly string[] GoodPaceLines =
    {
        "You're up on your best pace right now - nice, keep that up.",
        "That's quick. Whatever you just did, keep doing it.",
        "Good sector, you're flying.",
    };

    private static readonly string[] OffPaceLines =
    {
        "A bit off the pace this lap. No drama, reset for the next one.",
        "Lost a bit of time there, no big deal, refocus.",
    };

    // General banter - not tied to any specific telemetry, just personality/character.
    // This is the stuff that's supposed to make it feel like a person, not a status readout.
    private static readonly string[] BanterLines =
    {
        "Car's looking good from here, keep it smooth.",
        "Long race ahead, pace yourself, we've got time.",
        "Talk to me if anything feels off with the car.",
        "Quiet out there. I like quiet. Quiet means no problems.",
        "You know, I could go for a coffee right about now. Anyway, keep pushing.",
        "Everything's reading normal on my end. Living the dream.",
        "Don't mind me, just staring at numbers over here.",
        "Whenever you're ready to make this interesting, I'm here for it.",
        "This is the boring part of my job. Enjoy it while it lasts.",
        "No news is good news. Keep it up.",
    };

    public void MaybeChat(GameState state, SpeechOutput speech, PitWatch.Config config)
    {
        // Guard against firing in menus/replays/pits or with stale data: require actual
        // movement, on the racing surface, and a real current lap in progress.
        if (!state.IsGameRunning) return;
        if (state.IsInPit) return;
        if (state.SpeedKmh < 20) return;
        if (state.WheelsOffTrack > 0) return;
        if (state.CurrentLap < 1) return;

        float baseMultiplier = config.Chattiness switch
        {
            "Quiet" => 0f,      // effectively off, unless overridden live
            "Chatty" => 0.4f,   // fires much more often
            _ => 1f,
        };
        float multiplier = _runtimeMultiplier ?? baseMultiplier;
        if (multiplier <= 0f) return;

        if (DateTime.UtcNow < _nextChatterTime) return;

        // Stay quiet if something else was just said - prevents contradicting an
        // event callout that fired moments ago.
        if (DateTime.UtcNow - speech.LastSpokenAt < QuietPeriodAfterOtherSpeech) return;

        speech.Speak(PickLine(state));
        int baseSeconds = _rng.Next(90, 210);
        _nextChatterTime = DateTime.UtcNow.AddSeconds(baseSeconds * multiplier);
    }

    private string PickLine(GameState state)
    {
        // ~40% chance of a pace-aware comment when we have real data for it, otherwise
        // (or the other 60% of the time) fall back to general banter - keeps it feeling
        // aware of the race without EVERY line being a pace readout.
        bool hasPaceData = state.WheelsOffTrack == 0 && state.BestLapTimeSeconds > 0 && state.CurrentLapTimeSeconds > 0;

        if (hasPaceData && _rng.NextDouble() < 0.4)
        {
            float delta = state.CurrentLapTimeSeconds - state.BestLapTimeSeconds;
            if (delta < -0.3f) return Pick(GoodPaceLines);
            if (delta > 2f) return Pick(OffPaceLines);
        }

        return Pick(BanterLines);
    }

    private string Pick(string[] options) => options[_rng.Next(options.Length)];
}
