using System.Linq;

namespace PitWatch.Commands;

/// <summary>
/// Turns raw ACC damage values into a specific, actionable spoken report.
///
/// INDEX 4 ("centre") DROPPED FROM LOCATION LISTING: real test data showed it reading
/// identical to the front value on a front-only hit (both 41.79), suggesting it's an
/// aggregate/overall figure rather than a genuine fifth location. Still factored into
/// severity since it's real signal, just not narrated as its own body part anymore.
///
/// CALIBRATION: thresholds updated from one real data point (a "small" hit reading 41.79
/// on a roughly 0-100 scale) - light/moderate/heavy boundaries shifted up accordingly.
/// Still not fully proven - if severity still sounds off, compare "debug damage" output
/// against ACC's own in-game damage HUD numbers for the same hit and send both over.
///
/// SUSPENSION NOTE: ACC's basic shared memory doesn't expose a distinct "suspension
/// damage" flag separate from general body damage, so this can't specifically confirm
/// suspension issues - that's a real gap, not something the advice text should imply.
/// </summary>
public static class DamageFormatter
{
    private static readonly string[] PartNames = { "front", "rear", "left side", "right side" };

    public static string FormatAutomaticCallout(float[] damage)
    {
        var parts = DamagedParts(damage);
        if (parts.Count == 0) return "No damage detected.";

        float maxDamage = damage.Max();
        string advice = maxDamage switch
        {
            < 15f => "Minor, no need to pit for this.",
            < 45f => "Pit for it at your next scheduled stop, don't come in early just for this.",
            _ => "That's enough to pit for now, don't wait."
        };

        return $"Picked up damage: {string.Join(", ", parts)}. {advice}";
    }

    public static string FormatQuery(float[] damage)
    {
        var parts = DamagedParts(damage);
        return parts.Count == 0 ? "No damage detected." : "You've got " + string.Join(", ", parts) + ".";
    }

    private static List<string> DamagedParts(float[] damage)
    {
        var result = new List<string>();
        for (int i = 0; i < damage.Length && i < PartNames.Length; i++)
        {
            if (damage[i] <= 0) continue;
            string severity = damage[i] switch
            {
                < 15f => "light",
                < 45f => "moderate",
                _ => "heavy"
            };
            result.Add($"{severity} damage on the {PartNames[i]}");
        }
        return result;
    }
}
