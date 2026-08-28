using PitWatch.Models;

namespace PitWatch.Telemetry;

public interface ITelemetryProvider
{
    /// <summary>Human readable name, e.g. "ACC" or "LMU".</summary>
    string Name { get; }

    /// <summary>Returns true if the game's shared memory is currently available (game running).</summary>
    bool IsAvailable();

    /// <summary>Reads the current telemetry snapshot. Returns a GameState with IsGameRunning=false if unavailable.</summary>
    GameState ReadState();
}
