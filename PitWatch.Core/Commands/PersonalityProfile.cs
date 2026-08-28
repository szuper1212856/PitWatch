namespace PitWatch.Commands;

/// <summary>
/// One place that defines what each personality actually sounds like, so the AI's system
/// prompt and the hand-written scripted lines (overtake/roast/damage/idle chatter) stay
/// consistent with each other instead of the AI having one tone and the canned lines having
/// another.
/// </summary>
public static class PersonalityProfile
{
    public static string SystemPromptStyle(string personality) => personality switch
    {
        "Kind" => "You're warm, encouraging, and patient. You never mock mistakes - you reframe them constructively. " +
                  "Genuine enthusiasm when things go well.",
        "Mean" => "You're blunt, sarcastic, and hold nothing back. You mock mistakes openly and aren't afraid to be harsh. " +
                  "Still fundamentally on the driver's side, just with zero sugar-coating.",
        "Professional" => "You're calm, precise, and strictly business - like a real F1 race engineer on team radio. " +
                           "Minimal personality, maximum clarity. No jokes, no banter, just accurate calls.",
        _ => "You've got real personality - dry humor, genuine reactions, banter, maybe some light trash talk if " +
             "they're driving badly or praise if they're on it - like an actual race engineer, not a flat readout.",
    };

    public static string[] OvertakenLines(string personality) => personality switch
    {
        "Kind" => new[]
        {
            "They got past - no worries, plenty of race left.",
            "Lost a spot there, shake it off and refocus.",
            "That's alright, get back to your rhythm.",
        },
        "Mean" => new[]
        {
            "Pathetic. They just drove right past you.",
            "That was embarrassing. Do better.",
            "You just got dropped. Unbelievable.",
            "Was that your best defense? Because that was bad.",
        },
        "Professional" => new[]
        {
            "Position lost. Focus on the next lap.",
            "You've been passed. Reassess your line.",
        },
        _ => new[]
        {
            "Ouch. That one got past you easy.",
            "You just got sent. Embarrassing.",
            "That's a position gone. What was that?",
            "They just walked right by you. Wake up.",
            "Well that was soft. Get 'em back.",
        },
    };

    public static string[] OvertakeLines(string personality) => personality switch
    {
        "Kind" => new[]
        {
            "Beautiful move! Really well done.",
            "That's it, great overtake!",
            "Lovely, you're through - keep that up.",
        },
        "Mean" => new[]
        {
            "Finally, something decent from you.",
            "Not bad. Don't get used to it.",
            "There it is. About time.",
        },
        "Professional" => new[]
        {
            "Position gained. Good work.",
            "Overtake complete, maintain focus.",
        },
        _ => new[]
        {
            "Nice move, position gained!",
            "That's a pass, well done.",
            "Good overtake, keep pushing.",
        },
    };
}
