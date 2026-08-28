using System.Linq;

namespace PitWatch.Models;

public enum SessionKind
{
    Practice,
    Qualifying,
    Race,
    Hotlap,
    TimeAttack,
    Other,
}


/// <summary>
/// Normalized snapshot of telemetry, regardless of which game it came from.
/// Both AccTelemetryReader and LmuTelemetryReader produce this same shape,
/// so the rest of the app (callouts, AI context) never needs to know which sim is running.
/// </summary>
public class GameState
{
    public bool IsGameRunning { get; set; }
    public string GameName { get; set; } = "Unknown";

    /// <summary>Track and car, read from ACC's static page. Without these, saved sessions
    /// are just dates and lap times with no way to tell a Spa lap from a Monza one.</summary>
    public string TrackName { get; set; } = "";
    public string CarModel { get; set; } = "";

    /// <summary>Real tank capacity from the game, so fuel planning stops guessing.</summary>
    public float MaxFuelLiters { get; set; }

    /// <summary>
    /// What the current game actually provides. The two sims expose very different
    /// amounts of data, and showing empty ACC-shaped panels while running LMU just looks
    /// broken - the UI hides what can't be filled instead of displaying permanent blanks.
    /// </summary>
    public bool HasSectorData { get; set; } = true;
    public bool HasTyreData { get; set; } = true;
    public bool HasDamageData { get; set; } = true;
    public bool HasPositionData { get; set; } = true;
    public bool HasSessionTimeData { get; set; } = true;
    public bool HasGForceData { get; set; } = true;
    public bool SupportsTrackMap { get; set; } = true;

    // Fuel
    public float FuelLiters { get; set; }
    public float FuelPerLap { get; set; }
    public int EstimatedLapsOfFuelLeft { get; set; }

    // Tyres (FL, FR, RL, RR)
    public float[] TyreWearPercent { get; set; } = new float[4];
    public float[] TyreWearRaw { get; set; } = new float[4]; // untransformed, for debugging the formula
    public float[] TyreTempCelsius { get; set; } = new float[4];
    public float[] TyrePressurePsi { get; set; } = new float[4];

    public float SpeedKmh { get; set; }
    public float Throttle { get; set; }
    public float Brake { get; set; }
    public float SteerAngle { get; set; }
    public int Gear { get; set; }
    public float LapProgress { get; set; } // 0.0 to 1.0, how far through the current lap
    public int CurrentSectorIndex { get; set; } // 0, 1, 2 for sectors 1-3
    public int Rpm { get; set; }
    public float HeadingRad { get; set; }
    public int WheelsOffTrack { get; set; }
    public bool IsInPit { get; set; }
    public int SessionFlagRaw { get; set; }

    // Best-effort ACC flag enum mapping from community references - not verified against
    // your actual game yet. If callouts say the wrong flag color, say "debug flag" when
    // you see a real flag in-game and tell me the number it reports plus what flag it
    // actually was, and I'll correct this table with real data instead of guessing again.
    private static readonly Dictionary<int, string> FlagNames = new()
    {
        { 0, "no flag" },
        { 1, "blue flag" },
        { 2, "yellow flag" },
        { 3, "black flag" },
        { 4, "white flag" },
        { 5, "checkered flag" },
        { 6, "penalty flag" },
        { 7, "green flag" },
        { 8, "orange flag" },
    };

    public string SessionFlagName => FlagNames.TryGetValue(SessionFlagRaw, out var name) ? name : $"flag {SessionFlagRaw}";
    public float ImpactG { get; set; }
    public float GForceLateral { get; set; }
    public float GForceLongitudinal { get; set; } // magnitude of G-force this frame, used for crash detection

    // Timing
    public float LastLapTimeSeconds { get; set; }
    public float BestLapTimeSeconds { get; set; }
    public float CurrentLapTimeSeconds { get; set; }
    public int CurrentLap { get; set; }
    public int TotalLaps { get; set; } // 0 if unknown/time-limited session

    // Position / race
    public int Position { get; set; }
    public int TotalCars { get; set; }
    public float GapToCarAheadSeconds { get; set; }
    public float GapToCarBehindSeconds { get; set; }

    // Damage / flags
    public float[] CarDamageRaw { get; set; } = new float[5]; // [front, rear, left, right, centre] - best-effort index mapping, verify with debug dump
    public bool HasDamage => CarDamageRaw.Any(d => d > 0f);
    public string SessionFlag { get; set; } = "Green";
    public string SessionType { get; set; } = "Unknown"; // Practice/Qual/Race

    // Raw ACC status/session ints - these fields sit early in the graphics struct (right
    // after packetId), well before the large opponent-car arrays we haven't verified the
    // size of yet, so they're safe to trust. status: 0=off, 1=replay, 2=live, 3=pause.
    // session: 0=practice, 1=qualifying, 2=race (best-effort mapping per public ACC docs).
    public int SessionStatusRaw { get; set; }
    public int SessionTypeRaw { get; set; }
    public float SessionTimeLeftSeconds { get; set; }
    public bool IsLiveRaceSession => SessionStatusRaw == 2 && SessionTypeRaw == 2;

    /// <summary>
    /// ACC session type values. Practice and qualifying behave very differently from a
    /// race - position changes are meaningless when everyone is on their own programme,
    /// so the engineer shouldn't react to them as though places are being lost.
    /// </summary>
    public SessionKind Kind => SessionTypeRaw switch
    {
        0 => SessionKind.Practice,
        1 => SessionKind.Qualifying,
        2 => SessionKind.Race,
        3 or 8 => SessionKind.Hotlap,
        4 => SessionKind.TimeAttack,
        _ => SessionKind.Other,
    };

    public bool IsRace => Kind == SessionKind.Race;

    /// <summary>True when lap pace matters but race position doesn't.</summary>
    public bool IsPracticeLike => Kind is SessionKind.Practice or SessionKind.Qualifying
                                       or SessionKind.Hotlap or SessionKind.TimeAttack;

    public string KindName => Kind switch
    {
        SessionKind.Practice => "Practice",
        SessionKind.Qualifying => "Qualifying",
        SessionKind.Race => "Race",
        SessionKind.Hotlap => "Hotlap",
        SessionKind.TimeAttack => "Time Attack",
        _ => "Session",
    };

    /// <summary>
    /// Produces a compact plain-English summary to feed the AI as context,
    /// so Gemini answers using real live data instead of guessing.
    /// </summary>
    public string ToPromptContext()
    {
        return $"""
            Game: {GameName}
            Session: {SessionType}, flag: {SessionFlag}
            Lap: {CurrentLap}, current sector: {CurrentSectorIndex + 1}
            Current lap time: {CurrentLapTimeSeconds:F1}s, last lap: {LastLapTimeSeconds:F1}s, best lap: {BestLapTimeSeconds:F1}s
            Position: {Position} of {TotalCars}
            Gap ahead: {GapToCarAheadSeconds:F1}s, gap behind: {GapToCarBehindSeconds:F1}s
            Fuel: {FuelLiters:F1}L remaining, ~{FuelPerLap:F2}L per lap, about {EstimatedLapsOfFuelLeft} laps of fuel left
            Session time remaining: {SessionTimeLeftSeconds:F0}s
            Tyre temps C (FL/FR/RL/RR): {TyreTempCelsius[0]:F0}/{TyreTempCelsius[1]:F0}/{TyreTempCelsius[2]:F0}/{TyreTempCelsius[3]:F0}
            Tyre pressures PSI (FL/FR/RL/RR): {TyrePressurePsi[0]:F1}/{TyrePressurePsi[1]:F1}/{TyrePressurePsi[2]:F1}/{TyrePressurePsi[3]:F1}
            Speed: {SpeedKmh:F0} km/h, in pit: {IsInPit}
            Damage present: {HasDamage}
            NOTE: tyre WEAR is not available from this game's telemetry - never claim to know it.
            """;
    }

    /// <summary>Formats seconds as race-style M:SS.f, e.g. 138.8 -> "2:18.8".</summary>
    public static string FormatLapTime(float seconds)
    {
        // Upper bound guards against ACC's sentinel value for "no time set yet" (e.g.
        // before a valid best lap exists this session) - it's not just 0 or negative,
        // it's sometimes a large placeholder that slips past a <=0 check alone. No real
        // lap is anywhere near an hour, so anything past that is treated as unset.
        if (seconds <= 0 || seconds > 3600) return "no time set";
        int minutes = (int)(seconds / 60);
        float remainder = seconds - minutes * 60;
        return $"{minutes}:{remainder:00.0}";
    }

    /// <summary>Speaks position naturally: "leading", "P2", "P3", or "P7" etc.</summary>
    public static string SpokenPosition(int position) => position switch
    {
        1 => "P1, you're leading",
        2 => "P2",
        3 => "P3",
        <= 0 => "position unknown",
        _ => $"P{position}"
    };
}
