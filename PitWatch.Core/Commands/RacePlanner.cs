using System.Linq;
using PitWatch.Models;

namespace PitWatch.Commands;

public record RacePlan(
    int RaceLaps,
    float FuelPerLap,
    float TotalFuelNeeded,
    float StartingFuel,
    int Stops,
    float FuelPerStop,
    int[] SuggestedPitLaps,
    float TankCapacity,
    string Summary,
    string[] Details);

/// <summary>
/// Works out what to actually do in the race, using pace and consumption learned during
/// practice. This is the part that was missing: the live strategy engine answers "will I
/// make it from here", but before the race you need different numbers entirely - how much
/// fuel to start with, how many stops, and how much to put in at each one.
///
/// Everything here is derived from data measured during the session plus the rules the
/// driver entered, so it works in practice where the game reports no race length at all.
/// </summary>
public static class RacePlanner
{
    /// <summary>Spare fuel carried beyond the exact calculation, to cover safety cars,
    /// a slow formation lap, and consumption being slightly higher in race conditions.</summary>
    private const float SafetyMarginLaps = 1.0f;

    private const float FallbackTankCapacity = 100f;

    public static RacePlan? Build(GameState state, RaceRules rules, float? measuredFuelPerLap, float? bestLapSeconds)
    {
        float fuelPerLap = measuredFuelPerLap ?? state.FuelPerLap;
        if (fuelPerLap <= 0) return null;

        // Race length: prefer what the driver told us, since in practice the game reports
        // nothing useful about the upcoming race.
        int raceLaps;
        if (rules.RaceLengthLaps is int laps && laps > 0)
        {
            raceLaps = laps;
        }
        else if (rules.RaceLengthMinutes is int mins && mins > 0)
        {
            float lapTime = bestLapSeconds ?? state.BestLapTimeSeconds;
            if (lapTime <= 0 || lapTime > 3600) return null;

            // Race pace is a little slower than a qualifying best - traffic, fuel load,
            // tyre wear. Padding the lap time here avoids under-fuelling the car.
            float racePace = lapTime * 1.02f;
            raceLaps = (int)Math.Ceiling(mins * 60f / racePace) + 1; // +1 for the lap you finish on
        }
        else
        {
            return null;
        }

        // Prefer what the driver told us, then what the game reports, and only fall back
        // to a guess if neither is available.
        float tank = rules.TankCapacityLiters
                     ?? (state.MaxFuelLiters > 10 ? state.MaxFuelLiters : FallbackTankCapacity);
        float exactFuel = raceLaps * fuelPerLap;
        float totalFuel = exactFuel + fuelPerLap * SafetyMarginLaps;

        // How many stops the fuel alone forces.
        int fuelStops = totalFuel <= tank ? 0 : (int)Math.Ceiling(totalFuel / tank) - 1;

        // Regulations can force a stop even when fuel wouldn't.
        bool mandatory = rules.MandatoryPitStop || rules.MandatoryTyreChange
                         || rules.MandatoryRefuelling || rules.MandatoryDriverChange;
        int stops = Math.Max(fuelStops, mandatory ? 1 : 0);

        float startingFuel;
        float fuelPerStop;
        var pitLaps = new List<int>();

        if (stops == 0)
        {
            startingFuel = Math.Min(totalFuel, tank);
            fuelPerStop = 0;
        }
        else
        {
            // Split the race into equal stints so no stint runs the tank dry and the
            // stops fall at sensible, evenly spaced points.
            float lapsPerStint = raceLaps / (float)(stops + 1);
            float stintFuel = lapsPerStint * fuelPerLap + fuelPerLap * SafetyMarginLaps;

            startingFuel = Math.Min(stintFuel, tank);
            fuelPerStop = Math.Min(stintFuel, tank);

            for (int i = 1; i <= stops; i++)
            {
                pitLaps.Add((int)Math.Round(lapsPerStint * i));
            }

            // Honour an explicit pit window if the driver set one.
            if (rules.MinimumPitWindowLap is int min || rules.MaximumPitWindowLap is int max)
            {
                for (int i = 0; i < pitLaps.Count; i++)
                {
                    if (rules.MinimumPitWindowLap is int lo) pitLaps[i] = Math.Max(pitLaps[i], lo);
                    if (rules.MaximumPitWindowLap is int hi) pitLaps[i] = Math.Min(pitLaps[i], hi);
                }
            }
        }

        var details = new List<string>
        {
            $"Race length: {raceLaps} laps"
                + (rules.RaceLengthMinutes.HasValue ? $" (estimated from {rules.RaceLengthMinutes} minutes)" : ""),
            $"Burning {fuelPerLap:F2} L per lap, measured over your running",
            $"Total needed: {totalFuel:F1} L including about one lap spare",
        };

        if (stops == 0)
        {
            details.Add("No stop needed - it fits in one tank");
        }
        else
        {
            details.Add($"{stops} stop{(stops == 1 ? "" : "s")}"
                + (fuelStops == 0 ? " (required by the rules, not by fuel)" : ""));
            details.Add($"Add {fuelPerStop:F1} L at each stop");
            details.Add($"Pit around lap {string.Join(" and ", pitLaps)}");
        }

        if (!rules.TankCapacityLiters.HasValue)
        {
            details.Add(state.MaxFuelLiters > 10
                ? $"Tank capacity {tank:F0} L, read from the game"
                : $"Assuming a {FallbackTankCapacity:F0} L tank - enter the real capacity for a better plan");
        }

        string summary = stops == 0
            ? $"Start with {startingFuel:F1} litres and run to the end - no stop needed."
            : $"Start with {startingFuel:F1} litres, pit on lap {string.Join(" and ", pitLaps)}, "
              + $"adding {fuelPerStop:F1} litres each time.";

        return new RacePlan(raceLaps, fuelPerLap, totalFuel, startingFuel, stops, fuelPerStop,
            pitLaps.ToArray(), tank, summary, details.ToArray());
    }
}
