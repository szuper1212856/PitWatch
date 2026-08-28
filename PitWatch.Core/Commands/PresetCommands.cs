using System.Linq;
using System.Text.RegularExpressions;
using PitWatch.Models;

namespace PitWatch.Commands;

/// <summary>
/// Preset callouts computed directly from telemetry - no AI call needed.
/// Returns null if the phrase doesn't match a known preset, so the caller
/// can fall back to sending the question to the AI instead.
///
/// WHOLE-WORD MATCHING: keyword checks use word boundaries rather than plain substring
/// matching. Plain Contains() caused real misfires - "I might just retire at this point"
/// matched "tire" inside "re-TIRE-" and got answered with tyre pressures. Anything
/// conversational that merely mentions a keyword-like fragment should fall through to
/// the AI instead of being hijacked by a preset.
/// </summary>
public static class PresetCommands
{
    private static bool Has(string text, params string[] words)
        => words.Any(w => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b"));

    public static string? TryHandle(string spokenText, GameState state, LapAnalyzer? lapAnalyzer = null,
        RaceRules? rules = null)
    {
        var lower = spokenText.ToLowerInvariant();

        // Race regulations the driver tells us about (mandatory stops, race length, pit
        // windows). Checked before everything else, since phrases like "mandatory pit stop"
        // would otherwise be swallowed by the strategy keyword handler below and answered
        // instead of recorded.
        if (rules != null)
        {
            var ruleReply = RaceRulesParser.TryParse(spokenText, rules);
            if (ruleReply != null) return ruleReply;

            if (Has(lower, "rules", "regulations") || Has(lower, "what rules", "race rules"))
                return rules.Describe();
        }

        if (!state.IsGameRunning)
            return "I don't see a live session right now.";

        var text = spokenText.ToLowerInvariant();

        if (Has(text, "debug"))
            return HandleDebug(text, state);

        if (Has(text, "slow", "sector", "improve") || Has(text, "losing time", "lose time"))
        {
            return lapAnalyzer?.WhereAmISlow() ?? "Lap analysis isn't available right now.";
        }

        // Race planning - "how much fuel should I start with", "what's my race plan".
        // Distinct from the live strategy below, which answers "will I make it from here".
        if (rules != null && (Has(text, "plan") || Has(text, "start with", "starting fuel")
            || (Has(text, "fuel") && Has(text, "start", "race"))))
        {
            var plan = RacePlanner.Build(state, rules, state.FuelPerLap, state.BestLapTimeSeconds);
            if (plan == null)
            {
                return "I need two things for a race plan: a few laps of running to measure your fuel use, "
                     + "and the race length. Set the race length on the Strategy tab or just tell me, "
                     + "for example \"60 minute race\".";
            }
            return plan.Summary;
        }

        if (Has(text, "strategy") || (Has(text, "pit", "box") && Has(text, "when", "should")))
        {
            var plan = StrategyEngine.Build(state, rules);
            return plan?.Summary ?? "I need one more full lap to measure your fuel burn before I can call a strategy.";
        }

        if (Has(text, "save", "saving") && Has(text, "fuel"))
        {
            var plan = StrategyEngine.Build(state, rules);
            if (plan == null) return "Need another lap of fuel data first.";
            if (plan.SaveNeededPerLap is > 0 and < 0.3f)
                return $"Save {plan.SaveNeededPerLap:F2} liters a lap - lift about fifty metres earlier into the heavy braking zones and you'll make it to the flag.";
            if (plan.FuelMargin >= 0)
                return "No need to save, you've got enough to finish.";
            return "Saving won't cover it - you're too far short. You'll need to stop.";
        }

        if (Has(text, "theoretical") || (Has(text, "best") && Has(text, "possible")))
        {
            var theoretical = lapAnalyzer?.TheoreticalBest;
            var best = lapAnalyzer?.BestLapTime;
            if (theoretical == null) return "Need a clean time in all three sectors before I can work that out.";
            string msg = $"Theoretical best is {GameState.FormatLapTime(theoretical.Value)}";
            if (best.HasValue) msg += $", your actual best is {GameState.FormatLapTime(best.Value)} - {best.Value - theoretical.Value:F1} on the table";
            return msg + ".";
        }

        if (Has(text, "stint", "stints"))
        {
            return "Stint details are on the Strategy tab - I'll also read out a summary automatically after each stop.";
        }

        if (Has(text, "delta", "ghost"))
        {
            var d = lapAnalyzer?.LiveGhostDelta;
            if (d == null) return "No reference lap yet - complete a clean lap first.";
            return d.Value < 0
                ? $"You're {-d.Value:F1} up on your best lap right now."
                : $"You're {d.Value:F1} down on your best lap right now.";
        }

        if ((Has(text, "finish") && Has(text, "fuel")) || Has(text, "enough fuel")
            || (Has(text, "fuel") && Has(text, "need")))
        {
            var plan = StrategyEngine.Build(state, rules);
            return plan?.Summary ?? $"You've got {state.FuelLiters:F1} liters - I need one more lap to measure burn rate.";
        }

        if (Has(text, "fuel"))
        {
            if (state.EstimatedLapsOfFuelLeft < 0)
                return $"You've got {state.FuelLiters:F1} liters. Need one more lap to calculate burn rate.";
            return $"{state.FuelLiters:F1} liters left, about {state.EstimatedLapsOfFuelLeft} laps of fuel.";
        }

        if (Has(text, "tyre", "tyres", "tire", "tires"))
        {
            return TyreAdvisor.Analyze(state) + " " + TyreAdvisor.Detail(state);
        }

        if (Has(text, "gap", "behind", "ahead"))
        {
            if (state.GapToCarAheadSeconds <= 0 && state.GapToCarBehindSeconds <= 0)
                return "Gap tracking isn't available - ACC doesn't give exact gaps through this data.";
            return $"Gap ahead {state.GapToCarAheadSeconds:F1} seconds, gap behind {state.GapToCarBehindSeconds:F1} seconds.";
        }

        if (Has(text, "position") || Has(text, "where am i"))
        {
            return state.TotalCars > 0
                ? $"You're {GameState.SpokenPosition(state.Position)} of {state.TotalCars}."
                : $"You're {GameState.SpokenPosition(state.Position)}.";
        }

        if (Has(text, "time left", "how long", "time remaining"))
        {
            if (state.SessionTimeLeftSeconds <= 0)
                return "This doesn't look like a timed session, or the race is already over.";
            int totalSeconds = (int)state.SessionTimeLeftSeconds;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return minutes > 0
                ? $"{minutes} minutes {seconds} seconds left."
                : $"{seconds} seconds left - almost there.";
        }

        if (Has(text, "lap time", "last lap"))
        {
            return $"Last lap {GameState.FormatLapTime(state.LastLapTimeSeconds)}, best lap {GameState.FormatLapTime(state.BestLapTimeSeconds)}.";
        }

        if (Has(text, "damage"))
        {
            return DamageFormatter.FormatAutomaticCallout(state.CarDamageRaw);
        }

        return null; // not a known preset - let the AI handle it
    }

    private static string HandleDebug(string text, GameState state)
    {
        if (Has(text, "tyre", "tyres", "tire", "tires"))
        {
            var raw = state.TyreWearRaw;
            return $"Raw tyre wear values: {raw[0]:F6}, {raw[1]:F6}, {raw[2]:F6}, {raw[3]:F6}.";
        }
        if (Has(text, "damage"))
        {
            var d = state.CarDamageRaw;
            return $"Raw damage values: {d[0]:F2}, {d[1]:F2}, {d[2]:F2}, {d[3]:F2}, {d[4]:F2}.";
        }
        if (Has(text, "flag"))
        {
            return $"Raw flag value: {state.SessionFlagRaw} (interpreted as: {state.SessionFlagName}).";
        }
        return "Debug options: 'debug tyres', 'debug damage', 'debug flag', 'debug broadcast'.";
    }
}
