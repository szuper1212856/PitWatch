using System.Text.Json;

namespace PitWatch.Commands;

/// <summary>
/// Holds the race rules the driver tells the engineer about.
///
/// WHY THIS IS NEEDED: things like "there's a mandatory pit stop" or "we must change tyres"
/// are race regulations, not telemetry - ACC doesn't expose them, and they completely
/// change what the right strategy is. A race with a mandatory stop means fuel calculations
/// alone give the wrong answer, because you're stopping regardless.
///
/// Kept per session rather than saved to disk, since these change race to race and stale
/// rules would silently produce wrong strategy calls.
/// </summary>
public class RaceRules
{
    public bool MandatoryPitStop { get; set; }
    public bool MandatoryTyreChange { get; set; }
    public bool MandatoryRefuelling { get; set; }
    public bool MandatoryRefuel { get; set; }
    public bool MandatoryDriverChange { get; set; }
    public int? RaceLengthMinutes { get; set; }
    public int? RaceLengthLaps { get; set; }
    public float? PitStopTimeLossSeconds { get; set; }
    public float? TankCapacityLiters { get; set; }
    public int? MinimumPitWindowLap { get; set; }
    public int? MaximumPitWindowLap { get; set; }

    public bool AnySet =>
        MandatoryPitStop || MandatoryTyreChange || MandatoryRefuel || MandatoryDriverChange
        || RaceLengthMinutes.HasValue || RaceLengthLaps.HasValue
        || PitStopTimeLossSeconds.HasValue || TankCapacityLiters.HasValue
        || MinimumPitWindowLap.HasValue || MaximumPitWindowLap.HasValue;

    public void Clear()
    {
        MandatoryPitStop = false;
        MandatoryTyreChange = false;
        MandatoryRefuelling = false;
        MandatoryRefuel = false;
        MandatoryDriverChange = false;
        RaceLengthMinutes = null;
        RaceLengthLaps = null;
        PitStopTimeLossSeconds = null;
        TankCapacityLiters = null;
        MinimumPitWindowLap = null;
        MaximumPitWindowLap = null;
    }

    /// <summary>Plain-English list of what's currently set, for reading back to the driver.</summary>
    public string Describe()
    {
        if (!AnySet) return "No race rules set. Tell me things like \"mandatory pit stop\" or \"60 minute race\" and I'll factor them in.";

        var parts = new List<string>();
        if (RaceLengthMinutes.HasValue) parts.Add($"{RaceLengthMinutes} minute race");
        if (RaceLengthLaps.HasValue) parts.Add($"{RaceLengthLaps} lap race");
        if (MandatoryPitStop) parts.Add("mandatory pit stop");
        if (MandatoryTyreChange) parts.Add("mandatory tyre change");
        if (MandatoryRefuelling) parts.Add("mandatory refuelling");
        if (MandatoryRefuel) parts.Add("mandatory refuelling");
        if (MandatoryDriverChange) parts.Add("mandatory driver change");
        if (MinimumPitWindowLap.HasValue || MaximumPitWindowLap.HasValue)
        {
            string from = MinimumPitWindowLap?.ToString() ?? "start";
            string to = MaximumPitWindowLap?.ToString() ?? "end";
            parts.Add($"pit window lap {from} to {to}");
        }
        if (PitStopTimeLossSeconds.HasValue) parts.Add($"{PitStopTimeLossSeconds:F0} second stop");
        if (TankCapacityLiters.HasValue) parts.Add($"{TankCapacityLiters:F0}L tank");

        return "Race rules: " + string.Join(", ", parts) + ".";
    }
}
