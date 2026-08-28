using System.Linq;
using PitWatch.Models;

namespace PitWatch.Commands;

/// <summary>
/// Reads real tyre core temperatures (confirmed accurate, unlike wear which ACC doesn't
/// expose) and reports whether they're in the working window, plus left/right imbalance
/// which usually points at setup or driving style.
///
/// TEMPERATURE WINDOW: GT3 slicks work best roughly 80-100C. That's a broad rule of thumb -
/// the true optimum varies by compound, car and conditions, and ACC doesn't tell us which
/// compound is fitted, so this is guidance rather than a precise readout.
/// </summary>
public static class TyreAdvisor
{
    private const float OptimalLow = 80f;
    private const float OptimalHigh = 100f;
    private const float ImbalanceThreshold = 12f;

    private static readonly string[] Corners = { "front left", "front right", "rear left", "rear right" };

    public static string Analyze(GameState state)
    {
        var t = state.TyreTempCelsius;
        if (t.All(v => v <= 0)) return "No tyre temperature data yet.";

        var issues = new List<string>();

        float avg = t.Average();
        if (avg < OptimalLow)
            issues.Add($"Tyres are cold at {avg:F0} degrees - push harder to build temperature.");
        else if (avg > OptimalHigh)
            issues.Add($"Tyres are overheating at {avg:F0} degrees - ease off and let them cool.");
        else
            issues.Add($"Tyres in the window at {avg:F0} degrees.");

        // Left vs right imbalance - common on tracks with mostly one-direction corners
        float leftAvg = (t[0] + t[2]) / 2f;
        float rightAvg = (t[1] + t[3]) / 2f;
        if (Math.Abs(leftAvg - rightAvg) > ImbalanceThreshold)
        {
            string hotter = leftAvg > rightAvg ? "left" : "right";
            issues.Add($"The {hotter} side is running {Math.Abs(leftAvg - rightAvg):F0} degrees hotter.");
        }

        // Front vs rear imbalance - points at balance/driving style
        float frontAvg = (t[0] + t[1]) / 2f;
        float rearAvg = (t[2] + t[3]) / 2f;
        if (frontAvg - rearAvg > ImbalanceThreshold)
            issues.Add("Fronts much hotter than rears - could be understeer or heavy braking.");
        else if (rearAvg - frontAvg > ImbalanceThreshold)
            issues.Add("Rears much hotter than fronts - could be wheelspin on exit.");

        return string.Join(" ", issues);
    }

    /// <summary>Returns a warning only when something's actually wrong, for automatic
    /// callouts - so it stays quiet when everything's fine instead of nagging.</summary>
    public static string? CheckForWarning(GameState state)
    {
        var t = state.TyreTempCelsius;
        if (t.All(v => v <= 0)) return null;

        float avg = t.Average();
        if (avg > OptimalHigh + 15f) return "Tyres are getting seriously hot - back off a bit.";
        if (avg < OptimalLow - 15f) return "Tyres are cold - be careful for a couple of corners.";
        return null;
    }

    public static string Detail(GameState state)
    {
        var t = state.TyreTempCelsius;
        var p = state.TyrePressurePsi;
        var parts = new List<string>();
        for (int i = 0; i < 4; i++)
            parts.Add($"{Corners[i]} {t[i]:F0}C at {p[i]:F1} PSI");
        return string.Join(", ", parts) + ".";
    }
}
