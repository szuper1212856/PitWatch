using System.Linq;
using PitWatch.Models;

namespace PitWatch.Commands;

/// <summary>
/// ACC/LMU shared memory gives current fuel liters but not "liters per lap" directly.
/// This tracks fuel level at each new lap to compute a rolling average burn rate.
/// </summary>
public class FuelTracker
{
    private float? _fuelAtLastLapStart;
    private int _lastSeenLap = -1;
    private readonly List<float> _recentLapBurns = new();
    private const int MaxHistory = 5;

    // Fallback tracking: session-wide average, used if per-lap detection hasn't produced
    // a usable number yet (e.g. missed a lap transition) so this never gets permanently
    // stuck on "calculating" for the whole session.
    private float? _fuelAtSessionStart;
    private int _lapAtSessionStart = -1;

    public void Update(GameState state)
    {
        if (!state.IsGameRunning) return;

        if (_lastSeenLap == -1)
        {
            _lastSeenLap = state.CurrentLap;
            _fuelAtLastLapStart = state.FuelLiters;
            _fuelAtSessionStart = state.FuelLiters;
            _lapAtSessionStart = state.CurrentLap;
            return;
        }

        if (state.CurrentLap != _lastSeenLap && _fuelAtLastLapStart.HasValue)
        {
            float burned = _fuelAtLastLapStart.Value - state.FuelLiters;
            if (burned > 0 && burned < 30) // sanity check, ignore refuel jumps
            {
                _recentLapBurns.Add(burned);
                if (_recentLapBurns.Count > MaxHistory)
                    _recentLapBurns.RemoveAt(0);
            }
            _lastSeenLap = state.CurrentLap;
            _fuelAtLastLapStart = state.FuelLiters;
        }

        if (_recentLapBurns.Count > 0)
        {
            state.FuelPerLap = _recentLapBurns.Average();
        }
        else if (_fuelAtSessionStart.HasValue && state.CurrentLap - _lapAtSessionStart >= 1)
        {
            // Fallback: overall average since session start, in case per-lap detection
            // missed the exact transition tick for some reason.
            float totalBurned = _fuelAtSessionStart.Value - state.FuelLiters;
            int lapsCompleted = state.CurrentLap - _lapAtSessionStart;
            state.FuelPerLap = totalBurned > 0 && lapsCompleted > 0 ? totalBurned / lapsCompleted : 0f;
        }
        else
        {
            state.FuelPerLap = 0f;
        }

        state.EstimatedLapsOfFuelLeft = state.FuelPerLap > 0
            ? (int)(state.FuelLiters / state.FuelPerLap)
            : -1; // -1 signals "not enough data yet"
    }
}
