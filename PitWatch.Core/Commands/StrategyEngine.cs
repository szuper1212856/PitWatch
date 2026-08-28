using System.Linq;
using PitWatch.Models;

namespace PitWatch.Commands;

public record StrategyPlan(
    int LapsRemaining,
    float FuelNeeded,
    float FuelMargin,
    int StopsRequired,
    int? BoxByLap,
    float? SaveNeededPerLap,
    string Summary);

/// <summary>
/// Fuel and pit strategy, including multi-stop planning for longer races and a
/// lift-and-coast saving target when you're marginally short.
///
/// FUEL ONLY: ACC doesn't expose tyre wear (confirmed by raw memory inspection), so this
/// can't factor tyre life into stop planning the way a real strategist would. Every
/// result says so rather than implying more certainty than the data supports.
/// </summary>
public static class StrategyEngine
{
    /// <summary>Typical usable tank for GT3 - used to work out how many stops a long race
    /// needs. Cars vary, so treat multi-stop counts as an estimate rather than exact.</summary>
    private const float AssumedTankCapacityLiters = 120f;

    public static StrategyPlan? Build(GameState state, RaceRules? rules = null)
    {
        if (state.FuelPerLap <= 0) return null;

        int lapsRemaining;
        if (state.TotalLaps > 0 && state.CurrentLap > 0)
        {
            lapsRemaining = Math.Max(state.TotalLaps - state.CurrentLap + 1, 0);
        }
        else if (state.SessionTimeLeftSeconds > 0 && state.LastLapTimeSeconds > 0)
        {
            lapsRemaining = (int)Math.Ceiling(state.SessionTimeLeftSeconds / state.LastLapTimeSeconds);
        }
        // Fall back to whatever race length the driver told us, for cases where the game
        // doesn't report it (some session types leave both fields empty).
        else if (rules?.RaceLengthLaps is int totalLaps && state.CurrentLap > 0)
        {
            lapsRemaining = Math.Max(totalLaps - state.CurrentLap + 1, 0);
        }
        else if (rules?.RaceLengthMinutes is int totalMins && state.LastLapTimeSeconds > 0)
        {
            float elapsed = state.CurrentLap * state.LastLapTimeSeconds;
            float remaining = totalMins * 60f - elapsed;
            lapsRemaining = Math.Max((int)Math.Ceiling(remaining / state.LastLapTimeSeconds), 0);
        }
        else
        {
            return null;
        }

        float fuelNeeded = lapsRemaining * state.FuelPerLap;
        float margin = state.FuelLiters - fuelNeeded;
        int lapsOfFuel = (int)(state.FuelLiters / state.FuelPerLap);

        // How many stops: how much extra fuel is needed beyond what's on board, divided
        // by roughly how much a full tank adds.
        int stops = margin >= 0 ? 0 : (int)Math.Ceiling(-margin / AssumedTankCapacityLiters);

        // A mandatory stop means you're stopping regardless of fuel - so the plan has to
        // account for at least one, otherwise the advice is simply wrong for that race.
        bool mandatory = rules?.MandatoryPitStop == true || rules?.MandatoryTyreChange == true
                          || rules?.MandatoryRefuelling == true || rules?.MandatoryDriverChange == true;
        if (mandatory) stops = Math.Max(stops, 1);

        int? boxByLap = margin >= 0 ? null : state.CurrentLap + lapsOfFuel;

        // With a mandatory stop and enough fuel, the deadline is the pit window closing
        // rather than running dry.
        if (mandatory && boxByLap == null)
            boxByLap = rules?.MaximumPitWindowLap;

        // Fuel saving: how much less per lap you'd need to burn to avoid stopping at all.
        float? savePerLap = null;
        if (margin < 0 && lapsRemaining > 0)
        {
            float targetPerLap = state.FuelLiters / lapsRemaining;
            savePerLap = state.FuelPerLap - targetPerLap;
        }

        string summary = BuildSummary(state, lapsRemaining, fuelNeeded, margin, stops, boxByLap, savePerLap, rules);
        return new StrategyPlan(lapsRemaining, fuelNeeded, margin, stops, boxByLap, savePerLap, summary);
    }

    private static string BuildSummary(GameState state, int lapsRemaining, float fuelNeeded,
        float margin, int stops, int? boxByLap, float? savePerLap, RaceRules? rules)
    {
        bool mandatory = rules?.MandatoryPitStop == true || rules?.MandatoryTyreChange == true
                          || rules?.MandatoryRefuelling == true || rules?.MandatoryDriverChange == true;

        string mandatoryNote = "";
        if (mandatory)
        {
            var required = new List<string>();
            if (rules!.MandatoryPitStop) required.Add("a pit stop");
            if (rules.MandatoryTyreChange) required.Add("a tyre change");
            if (rules.MandatoryRefuelling) required.Add("refuelling");
            if (rules.MandatoryDriverChange) required.Add("a driver change");
            mandatoryNote = $" You still have {string.Join(" and ", required)} to complete";

            if (rules.MaximumPitWindowLap.HasValue)
                mandatoryNote += $" - window closes on lap {rules.MaximumPitWindowLap}";
            mandatoryNote += ".";
        }

        if (margin >= 0)
        {
            string fuelPart = $"{lapsRemaining} laps to go, you need {fuelNeeded:F1} liters and have {state.FuelLiters:F1} - "
                            + $"{margin:F1} spare.";
            string stopPart = mandatory
                ? mandatoryNote
                : " No fuel stop needed.";
            return fuelPart + stopPart + " Tyre wear isn't in ACC's data, so this is fuel only.";
        }

        string stopText = stops <= 1
            ? $"One stop needed - box by lap {boxByLap} at the latest."
            : $"{stops} stops needed - first one by lap {boxByLap}.";

        // Only suggest saving when it's a realistic ask. Beyond roughly 0.3L/lap you're
        // losing so much lap time that stopping is almost always faster anyway.
        string saveText = "";
        if (savePerLap is > 0 and < 0.3f)
        {
            saveText = $" Alternatively, save {savePerLap:F2} liters a lap by lifting and coasting and you can run to the end.";
        }

        return $"{lapsRemaining} laps to go, short by {-margin:F1} liters. {stopText}{saveText}{mandatoryNote} "
             + "Tyre wear isn't in ACC's data, so this is fuel only.";
    }

    /// <summary>Short version for the always-visible dashboard panel.</summary>
    public static string QuickSummary(GameState state, RaceRules? rules = null)
    {
        var plan = Build(state, rules);
        if (plan == null) return "Building fuel data...";
        return plan.FuelMargin >= 0
            ? $"{plan.LapsRemaining} laps left · {plan.FuelMargin:F1}L spare · no stop needed"
            : $"{plan.LapsRemaining} laps left · short {-plan.FuelMargin:F1}L · box by lap {plan.BoxByLap}";
    }
}
