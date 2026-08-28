using System.Linq;
using PitWatch.Models;

namespace PitWatch.Commands;

public record Stint(
    int StintNumber,
    int StartLap,
    int EndLap,
    float FuelUsed,
    List<float> LapTimes)
{
    public int LapCount => Math.Max(EndLap - StartLap + 1, 0);
    public float? AverageLap => LapTimes.Count > 0 ? LapTimes.Average() : null;
    public float? BestLap => LapTimes.Count > 0 ? LapTimes.Min() : null;

    /// <summary>Standard deviation of lap times - lower means more consistent driving.</summary>
    public float? Consistency
    {
        get
        {
            if (LapTimes.Count < 2) return null;
            float avg = LapTimes.Average();
            return MathF.Sqrt(LapTimes.Sum(t => (t - avg) * (t - avg)) / LapTimes.Count);
        }
    }
}

/// <summary>
/// Splits the session into stints at each pit stop and reports on each one, plus builds
/// the end-of-race summary the "race director" reads out.
/// </summary>
public class StintTracker
{
    private readonly List<Stint> _completed = new();
    private Stint? _current;
    private bool _wasInPit;
    private float _fuelAtStintStart;
    private int _lastLap = -1;
    private string? _pendingStintSummary;

    // Race-wide tracking for the director summary
    private int _startPosition = -1;
    private int _bestPosition = int.MaxValue;
    private int _worstPosition = 0;
    private int _overtakesMade;
    private int _timesOvertaken;
    private bool _tookDamage;

    public IReadOnlyList<Stint> Stints => _completed;

    public void Update(GameState state)
    {
        if (!state.IsGameRunning) return;

        if (_startPosition == -1 && state.Position > 0)
            _startPosition = state.Position;

        if (state.Position > 0)
        {
            if (state.Position < _bestPosition) _bestPosition = state.Position;
            if (state.Position > _worstPosition) _worstPosition = state.Position;
        }

        if (state.HasDamage) _tookDamage = true;

        _current ??= StartNewStint(state);

        // Lap completed - record its time against the current stint
        if (_lastLap != -1 && state.CurrentLap != _lastLap && state.LastLapTimeSeconds > 0
            && state.LastLapTimeSeconds < 3600)
        {
            _current.LapTimes.Add(state.LastLapTimeSeconds);
        }
        _lastLap = state.CurrentLap;

        // Entering the pits closes out the stint
        if (state.IsInPit && !_wasInPit && _current.LapTimes.Count > 0)
        {
            var closed = _current with { EndLap = state.CurrentLap, FuelUsed = _fuelAtStintStart - state.FuelLiters };
            _completed.Add(closed);
            _pendingStintSummary = SummarizeStint(closed);
            _current = null;
        }
        else if (!state.IsInPit && _wasInPit)
        {
            // Left the pits - start a fresh stint
            _current = StartNewStint(state);
        }

        _wasInPit = state.IsInPit;
    }

    private Stint StartNewStint(GameState state)
    {
        _fuelAtStintStart = state.FuelLiters;
        return new Stint(_completed.Count + 1, state.CurrentLap, state.CurrentLap, 0f, new List<float>());
    }

    public void RecordOvertake(bool madeByPlayer)
    {
        if (madeByPlayer) _overtakesMade++;
        else _timesOvertaken++;
    }

    /// <summary>Spoken summary after each completed stint, consumed on read.</summary>
    public string? TakeStintSummary()
    {
        var s = _pendingStintSummary;
        _pendingStintSummary = null;
        return s;
    }

    private static string SummarizeStint(Stint s)
    {
        var parts = new List<string> { $"Stint {s.StintNumber} done - {s.LapCount} laps" };
        if (s.AverageLap.HasValue) parts.Add($"average {GameState.FormatLapTime(s.AverageLap.Value)}");
        if (s.BestLap.HasValue) parts.Add($"best {GameState.FormatLapTime(s.BestLap.Value)}");
        if (s.FuelUsed > 0) parts.Add($"{s.FuelUsed:F1} liters used");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// The full post-race debrief - position change, pace, consistency, incidents.
    /// Deliberately longer and more detailed than mid-race callouts, since the race is
    /// over and there's no reason to keep it short.
    /// </summary>
    public string BuildRaceDirectorSummary(GameState state, LapAnalyzer analyzer)
    {
        var lines = new List<string>();

        int finish = state.Position;
        if (_startPosition > 0 && finish > 0)
        {
            int gained = _startPosition - finish;
            string movement = gained > 0 ? $"up {gained}" : gained < 0 ? $"down {-gained}" : "no change";
            lines.Add($"Started P{_startPosition}, finished P{finish} - {movement}.");
        }
        else if (finish > 0)
        {
            lines.Add($"Finished P{finish}.");
        }

        var best = analyzer.BestLapTime;
        var theoretical = analyzer.TheoreticalBest;
        if (best.HasValue)
        {
            string paceLine = $"Best lap {GameState.FormatLapTime(best.Value)}";
            if (theoretical.HasValue && best.Value - theoretical.Value > 0.05f)
                paceLine += $", theoretical best {GameState.FormatLapTime(theoretical.Value)} - {best.Value - theoretical.Value:F1} left on the table";
            lines.Add(paceLine + ".");
        }

        var allLaps = analyzer.Laps.Where(l => l.WasValid).Select(l => l.LapTime).ToList();
        if (allLaps.Count >= 3)
        {
            float avg = allLaps.Average();
            float stdev = MathF.Sqrt(allLaps.Sum(t => (t - avg) * (t - avg)) / allLaps.Count);
            string rating = stdev < 0.5f ? "very consistent" : stdev < 1.5f ? "reasonably consistent" : "inconsistent";
            lines.Add($"{allLaps.Count} laps, average {GameState.FormatLapTime(avg)}, {rating} - {stdev:F1} seconds spread.");
        }

        if (_completed.Count > 0)
            lines.Add($"{_completed.Count} pit stop{(_completed.Count == 1 ? "" : "s")}.");

        if (_overtakesMade > 0 || _timesOvertaken > 0)
            lines.Add($"{_overtakesMade} overtake{(_overtakesMade == 1 ? "" : "s")} made, passed {_timesOvertaken} time{(_timesOvertaken == 1 ? "" : "s")}.");

        if (_tookDamage) lines.Add("Picked up damage along the way.");

        return lines.Count > 0 ? string.Join(" ", lines) : "Race complete.";
    }

    public void Reset()
    {
        _completed.Clear();
        _current = null;
        _wasInPit = false;
        _lastLap = -1;
        _startPosition = -1;
        _bestPosition = int.MaxValue;
        _worstPosition = 0;
        _overtakesMade = 0;
        _timesOvertaken = 0;
        _tookDamage = false;
        _pendingStintSummary = null;
    }
}
