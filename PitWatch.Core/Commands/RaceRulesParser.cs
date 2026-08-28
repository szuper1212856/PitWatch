using System.Text.RegularExpressions;

namespace PitWatch.Commands;

/// <summary>
/// Turns things the driver says into race rules - "it's a 60 minute race with a mandatory
/// pit stop and tyre change" sets three separate rules in one go.
///
/// Deliberately forgiving about phrasing, since this gets used mid-session by voice where
/// nobody phrases things precisely. Returns null when nothing rule-like was found, so the
/// caller can fall through to normal question handling.
/// </summary>
public static class RaceRulesParser
{
    public static string? TryParse(string input, RaceRules rules)
    {
        var text = input.ToLowerInvariant();
        var applied = new List<string>();

        // Clearing rules - check first, since "no mandatory stop" also contains "mandatory stop"
        if (Regex.IsMatch(text, @"\b(clear|reset|forget)\b.*\b(rules?|strategy|race)\b"))
        {
            rules.Clear();
            return "Race rules cleared.";
        }

        bool negated = Regex.IsMatch(text, @"\b(no|not|isn'?t|aren'?t|without)\b");

        // Race length in minutes: "60 minute race", "it's 90 minutes", "race is 45 min"
        var mins = Regex.Match(text, @"\b(\d{1,3})\s*(?:minute|minutes|min|mins)\b");
        if (mins.Success && int.TryParse(mins.Groups[1].Value, out int m) && m is > 0 and <= 1440)
        {
            rules.RaceLengthMinutes = m;
            rules.RaceLengthLaps = null; // the two are mutually exclusive
            applied.Add($"{m} minute race");
        }

        // Race length in laps: "20 lap race", "it's 30 laps"
        var laps = Regex.Match(text, @"\b(\d{1,3})\s*laps?\b");
        if (laps.Success && int.TryParse(laps.Groups[1].Value, out int l) && l is > 0 and <= 999
            && !Regex.IsMatch(text, @"\b(window|between|from|until|by)\b")) // avoid catching pit window numbers
        {
            rules.RaceLengthLaps = l;
            rules.RaceLengthMinutes = null;
            applied.Add($"{l} lap race");
        }

        // Mandatory stop
        if (Regex.IsMatch(text, @"\b(mandatory|compulsory|required|must)\b.*\b(pit|stop|box)\b")
            || Regex.IsMatch(text, @"\b(pit|stop)\b.*\b(mandatory|compulsory|required)\b"))
        {
            rules.MandatoryPitStop = !negated;
            applied.Add(negated ? "no mandatory stop" : "mandatory pit stop");
        }

        // Mandatory tyre change
        if (Regex.IsMatch(text, @"\b(tyre|tire)s?\b.*\b(change|swap)\b")
            || Regex.IsMatch(text, @"\b(change|swap)\b.*\b(tyre|tire)s?\b"))
        {
            rules.MandatoryTyreChange = !negated;
            applied.Add(negated ? "no tyre change required" : "mandatory tyre change");
        }

        // Mandatory refuelling - note this is distinct from "you need fuel to finish".
        // A regulation can force you to add fuel even when you'd otherwise have enough.
        if (Regex.IsMatch(text, @"\b(refuel|refuelling|refueling|fuel\s*stop|add\s*fuel|take\s*fuel)\b")
            && Regex.IsMatch(text, @"\b(mandatory|compulsory|required|must|have to)\b"))
        {
            rules.MandatoryRefuelling = !negated;
            applied.Add(negated ? "no refuelling required" : "mandatory refuelling");
        }

        // Tank capacity: "tank is 105 liters", "120 litre tank"
        var tank = Regex.Match(text, @"\b(\d{1,3})\s*(?:l|liter|liters|litre|litres)\b");
        if (tank.Success && Regex.IsMatch(text, @"\b(tank|capacity|max fuel)\b")
            && int.TryParse(tank.Groups[1].Value, out int cap) && cap is > 10 and < 500)
        {
            rules.TankCapacityLiters = cap;
            applied.Add($"{cap} litre tank");
        }

        // Driver change (endurance)
        if (Regex.IsMatch(text, @"\bdriver\s*(change|swap)\b"))
        {
            rules.MandatoryDriverChange = !negated;
            applied.Add(negated ? "no driver change" : "mandatory driver change");
        }

        // Pit window: "pit window between lap 10 and 20", "window opens lap 8"
        var window = Regex.Match(text, @"window.*?\b(\d{1,3})\b.*?\b(\d{1,3})\b");
        if (window.Success
            && int.TryParse(window.Groups[1].Value, out int from)
            && int.TryParse(window.Groups[2].Value, out int to))
        {
            rules.MinimumPitWindowLap = from;
            rules.MaximumPitWindowLap = to;
            applied.Add($"pit window lap {from} to {to}");
        }

        // Stop duration: "stop takes 30 seconds", "25 second pit stop"
        var stopTime = Regex.Match(text, @"\b(\d{1,3})\s*(?:second|seconds|sec|secs)\b");
        if (stopTime.Success && Regex.IsMatch(text, @"\b(stop|pit|box)\b")
            && int.TryParse(stopTime.Groups[1].Value, out int secs) && secs is > 0 and < 300)
        {
            rules.PitStopTimeLossSeconds = secs;
            applied.Add($"{secs} second stop");
        }

        if (applied.Count == 0) return null;

        return "Got it - " + string.Join(", ", applied) + ". I'll factor that into strategy calls.";
    }
}
