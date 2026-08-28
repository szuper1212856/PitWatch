using System.Linq;
using PitWatch.Models;

namespace PitWatch.Commands;

/// <summary>One completed lap's data.</summary>
public record LapRecord(int LapNumber, float LapTime, float[] SectorTimes, bool WasValid);

/// <summary>
/// Tracks per-sector and per-position lap data, producing:
///  - real sector times and which sector lost time
///  - theoretical best (sum of your best individual sectors)
///  - personal best alerts for laps and individual sectors
///  - a live delta against your best lap (ghost comparison)
///  - coaching by comparing speed at matched track positions
///
/// HONEST LIMITATION ON COACHING: ACC doesn't give this app corner names or a corner map,
/// so coaching is expressed as a position through the lap ("around 40% through") plus what
/// the data shows (speed deficit). Real data-driven feedback, but it can't say "brake later
/// into Les Combes".
/// </summary>
public class LapAnalyzer
{
    private const int SectorCount = 3;
    private const int PositionBuckets = 60;

    private readonly float?[] _bestSectorTimes = new float?[SectorCount];
    private readonly List<(int Sector, float Time)> _currentLapSectors = new();
    private readonly List<LapRecord> _laps = new();

    // Ghost/coaching: speed and elapsed time sampled at each position bucket
    private float[]? _bestLapSpeeds;
    private float[]? _bestLapElapsed;
    private readonly float[] _currentSpeeds = new float[PositionBuckets];
    private readonly float[] _currentElapsed = new float[PositionBuckets];
    private readonly bool[] _currentFilled = new bool[PositionBuckets];

    private int _lastSectorIndex = -1;
    private float _lastSectorBoundaryTime;
    private int _lastLap = -1;

    private string? _pendingReport;
    private string? _latestReport;
    private readonly Queue<string> _alerts = new();

    public IReadOnlyList<LapRecord> Laps => _laps;
    public float? BestLapTime { get; private set; }
    public float?[] BestSectors => _bestSectorTimes;
    public float? TheoreticalBest =>
        _bestSectorTimes.All(s => s.HasValue) ? _bestSectorTimes.Sum(s => s!.Value) : null;

    /// <summary>Live delta vs your best lap at the same point on track. Null until a best exists.</summary>
    public float? LiveGhostDelta { get; private set; }

    public void Reset()
    {
        Array.Clear(_bestSectorTimes);
        _currentLapSectors.Clear();
        _laps.Clear();
        _alerts.Clear();
        _bestLapSpeeds = null;
        _bestLapElapsed = null;
        Array.Clear(_currentFilled);
        _lastSectorIndex = -1;
        _lastLap = -1;
        BestLapTime = null;
        LiveGhostDelta = null;
        _latestReport = null;
        _pendingReport = null;
    }

    public void Update(GameState state)
    {
        if (!state.IsGameRunning) return;

        if (_lastSectorIndex == -1)
        {
            _lastSectorIndex = state.CurrentSectorIndex;
            _lastSectorBoundaryTime = state.CurrentLapTimeSeconds;
            _lastLap = state.CurrentLap;
            return;
        }

        int bucket = Math.Clamp((int)(state.LapProgress * PositionBuckets), 0, PositionBuckets - 1);
        if (!_currentFilled[bucket] && state.CurrentLapTimeSeconds > 0)
        {
            _currentSpeeds[bucket] = state.SpeedKmh;
            _currentElapsed[bucket] = state.CurrentLapTimeSeconds;
            _currentFilled[bucket] = true;
        }

        LiveGhostDelta = (_bestLapElapsed != null && _bestLapElapsed[bucket] > 0 && state.CurrentLapTimeSeconds > 0)
            ? state.CurrentLapTimeSeconds - _bestLapElapsed[bucket]
            : null;

        if (state.CurrentLap != _lastLap)
        {
            CompleteLap(state);
            _lastLap = state.CurrentLap;
            _lastSectorIndex = state.CurrentSectorIndex;
            _lastSectorBoundaryTime = 0f;
            return;
        }

        if (state.CurrentSectorIndex != _lastSectorIndex)
        {
            float sectorTime = state.CurrentLapTimeSeconds - _lastSectorBoundaryTime;
            if (sectorTime > 1f && sectorTime < 300f && _lastSectorIndex is >= 0 and < SectorCount)
                _currentLapSectors.Add((_lastSectorIndex, sectorTime));

            _lastSectorIndex = state.CurrentSectorIndex;
            _lastSectorBoundaryTime = state.CurrentLapTimeSeconds;
        }
    }

    private void CompleteLap(GameState state)
    {
        float lapTime = state.LastLapTimeSeconds;
        bool valid = lapTime > 1f && lapTime < 3600f;

        var pbSectors = new List<int>();
        var deltas = new List<(int Sector, float Delta)>();

        foreach (var (sector, time) in _currentLapSectors)
        {
            if (_bestSectorTimes[sector].HasValue)
                deltas.Add((sector, time - _bestSectorTimes[sector]!.Value));

            if (!_bestSectorTimes[sector].HasValue || time < _bestSectorTimes[sector]!.Value)
            {
                _bestSectorTimes[sector] = time;
                pbSectors.Add(sector);
            }
        }

        bool isPbLap = valid && (!BestLapTime.HasValue || lapTime < BestLapTime.Value);
        if (isPbLap)
        {
            BestLapTime = lapTime;
            _bestLapSpeeds = (float[])_currentSpeeds.Clone();
            _bestLapElapsed = (float[])_currentElapsed.Clone();
        }

        var sectorTimes = new float[SectorCount];
        foreach (var (s, t) in _currentLapSectors)
            if (s < SectorCount) sectorTimes[s] = t;

        _laps.Add(new LapRecord(Math.Max(state.CurrentLap - 1, 0), lapTime, sectorTimes, valid));

        // Personal best alerts, queued separately from the per-lap report so both can fire.
        if (isPbLap)
            _alerts.Enqueue($"New personal best! {GameState.FormatLapTime(lapTime)}.");
        else if (pbSectors.Count > 0)
            _alerts.Enqueue($"Personal best in {string.Join(" and ", pbSectors.Select(s => $"sector {s + 1}"))}.");

        string report = BuildReport(isPbLap, pbSectors, deltas);
        _pendingReport = report;
        _latestReport = report;

        _currentLapSectors.Clear();
        Array.Clear(_currentFilled);
    }

    private string BuildReport(bool isPbLap, List<int> pbSectors, List<(int Sector, float Delta)> deltas)
    {
        if (isPbLap) return "That's your best lap so far.";
        if (deltas.Count == 0) return "First clean lap recorded - I'll compare from the next one.";

        float total = deltas.Sum(d => d.Delta);
        var worst = deltas.OrderByDescending(d => d.Delta).First();

        if (worst.Delta > 0.1f)
            return $"Lost {worst.Delta:F1} in sector {worst.Sector + 1}, {total:F1} off your best overall.";

        return total < 0 ? "Solid lap, matching your best pace." : "Clean lap, right on your best sectors.";
    }

    /// <summary>One-shot per-lap analysis line, consumed when spoken.</summary>
    public string? TakeLastLapReport()
    {
        var r = _pendingReport;
        _pendingReport = null;
        return r;
    }

    /// <summary>One-shot personal-best alert, consumed when spoken.</summary>
    public string? TakeNextAlert() => _alerts.Count > 0 ? _alerts.Dequeue() : null;

    /// <summary>Detailed on-demand analysis including theoretical best and coaching.</summary>
    public string WhereAmISlow()
    {
        if (_latestReport == null)
            return "Not enough lap data yet - complete a couple of clean laps first.";

        var parts = new List<string> { _latestReport };

        var theo = TheoreticalBest;
        if (theo.HasValue && BestLapTime.HasValue)
        {
            float gap = BestLapTime.Value - theo.Value;
            parts.Add(gap > 0.05f
                ? $"Your theoretical best is {GameState.FormatLapTime(theo.Value)}, {gap:F1} better than your actual best."
                : $"Theoretical best {GameState.FormatLapTime(theo.Value)} - you've already put it together.");
        }

        var coaching = GetCoachingTip();
        if (coaching != null) parts.Add(coaching);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Compares the last lap's speed at each track position against the best lap's and
    /// describes the biggest loss. Position-based, not corner-named - see class summary.
    /// </summary>
    public string? GetCoachingTip()
    {
        if (_bestLapSpeeds == null) return null;

        int worstBucket = -1;
        float worstDeficit = 0;
        for (int i = 0; i < PositionBuckets; i++)
        {
            if (!_currentFilled[i] || _bestLapSpeeds[i] <= 0) continue;
            float deficit = _bestLapSpeeds[i] - _currentSpeeds[i];
            if (deficit > worstDeficit) { worstDeficit = deficit; worstBucket = i; }
        }

        if (worstBucket < 0 || worstDeficit < 5f) return null;

        int percent = (int)(worstBucket * 100.0 / PositionBuckets);
        return $"Biggest speed loss around {percent}% through the lap, {worstDeficit:F0} km/h down on your best there.";
    }
}
