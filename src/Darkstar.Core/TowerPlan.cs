namespace Darkstar;

/// <summary>
/// Turning the airfields of a running mission into one tower radio each.
///
/// WHY THIS IS GENERATED AND NOT TYPED: a Caucasus mission has a dozen or more airfields. Typing a
/// radio entry per airfield by hand, with a distinct frequency each and the name spelled the way DCS
/// spells it, is the kind of task nobody does twice correctly.
///
/// TWO THINGS DECIDE THE WHOLE DESIGN, and both are limits rather than preferences:
///
/// 1. DCS-gRPC does NOT expose an airfield's radio frequency. The Airbase message carries a name, a
///    callsign, a coalition and a position - nothing else. Those frequencies live in the mission
///    file and never reach the scripting environment. So the frequencies here are INVENTED, assigned
///    from a base and a step. That is not a shortcoming: the bot transmits over SRS, not over DCS's
///    own ATIS, so it needs its own frequency plan anyway. But it does mean the pilots cannot look
///    the frequencies up anywhere - which is why the F10 announcement is not decoration. It is the
///    only way they learn them. See <see cref="FrequencyAnnouncer"/>.
///
/// 2. The wake word is SHARED across the towers, not the airfield name. "Batumi", "Kobuleti" and
///    "Senaki-Kolkhi" through a small English speech model, spoken by a German pilot, is exactly the
///    failure the position-based airfield resolution was built to avoid - and making it a wake word
///    would put it back on the critical path, where a miss means the bot never reacts at all. The
///    FREQUENCY identifies the airfield. The pilot says one word they can pronounce, and the bot
///    answers as "Batumi Tower".
/// </summary>
public static class TowerPlan
{
    /// <summary>
    /// More than this many generated towers is almost certainly a mistake - a map's full airfield
    /// list rather than the ones a mission actually uses. SRS also has its own view on how many
    /// radios a client may register, which this bot cannot ask about in advance.
    /// </summary>
    public const int MaxTowers = 20;

    /// <param name="AirfieldName">Exactly as DCS names it, which is what the airfield lookup matches on.</param>
    /// <param name="Callsign">What the bot calls itself on this frequency, e.g. "Batumi Tower".</param>
    public sealed record Tower(
        string AirfieldName,
        string Callsign,
        double FrequencyHz,
        string Modulation,
        double Lat,
        double Lon);

    /// <param name="Towers">The plan, in a stable order so regenerating gives the same frequencies.</param>
    /// <param name="Notes">What was skipped and why - shown to whoever pressed the button.</param>
    public sealed record Result(List<Tower> Towers, List<string> Notes)
    {
        public bool Any => Towers.Count > 0;
    }

    /// <summary>
    /// Works out one tower per airfield.
    /// </summary>
    /// <param name="airfields">What the mission reported.</param>
    /// <param name="config">Supplies the frequency plan and the callsign suffix.</param>
    /// <param name="reservedFrequencies">
    /// Frequencies already in use - the AWACS radio, a tanker, anything the operator configured by
    /// hand. Skipped rather than overwritten: two radios on one frequency means the second silently
    /// replaces the first, since sessions are keyed by frequency.
    /// </param>
    public static Result Plan(IEnumerable<Airfield> airfields, AppConfig config,
        IEnumerable<double>? reservedFrequencies = null)
    {
        var notes = new List<string>();
        var towers = new List<Tower>();

        // Sorted by the name DCS uses, so pressing the button twice assigns the same frequency to
        // the same airfield. Pilots write these down; they must not move on a whim.
        var ordered = airfields
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            notes.Add("The mission reported no airfields. Is a mission loaded, and is DCS-gRPC reachable?");
            return new Result(towers, notes);
        }

        var taken = new HashSet<long>((reservedFrequencies ?? Enumerable.Empty<double>())
            .Select(f => (long)Math.Round(f)));

        var step = Math.Abs(config.TowerPlanStepMHz) < 0.001 ? 0.5 : config.TowerPlanStepMHz;
        var next = config.TowerPlanBaseMHz;

        foreach (var airfield in ordered)
        {
            if (towers.Count >= MaxTowers)
            {
                notes.Add($"Stopped at {MaxTowers} towers - {ordered.Count - towers.Count} airfield(s) left out. " +
                          "Configure the ones your mission actually uses by hand instead.");
                break;
            }

            // Walk up the plan until a free slot is found, so a hand-configured AWACS frequency in
            // the middle of the range costs one slot rather than breaking the whole plan.
            long frequencyHz;
            var guard = 0;
            do
            {
                frequencyHz = (long)Math.Round(next * 1_000_000);
                next += step;
            }
            while (!taken.Add(frequencyHz) && ++guard < 1000);

            towers.Add(new Tower(
                airfield.Name,
                CallsignFor(airfield, config.TowerPlanCallsignSuffix),
                frequencyHz,
                string.IsNullOrWhiteSpace(config.TowerPlanModulation) ? "AM" : config.TowerPlanModulation.Trim().ToUpperInvariant(),
                airfield.Lat,
                airfield.Lon));
        }

        notes.Add($"{towers.Count} tower(s) planned from {Mhz(config.TowerPlanBaseMHz)} MHz " +
                  $"in steps of {Mhz(step)} MHz. The wake word stays the global one - the frequency " +
                  "identifies the airfield, so nobody has to pronounce its name.");

        return new Result(towers, notes);
    }

    /// <summary>
    /// "Batumi Tower". Uses the prettier name DCS supplies where there is one, and strips the
    /// hyphenated second half of names like "Senaki-Kolkhi" - a callsign is spoken on every reply,
    /// and the short form is what a controller would actually say.
    /// </summary>
    /// <summary>
    /// A frequency in MHz the way it is written everywhere else in this project: "133.000", with a
    /// dot, whatever the operator's Windows is set to. These notes are shown in the config editor
    /// next to the frequency fields and are copied into radio entries from there, so a comma would
    /// not just look wrong - it would be a different number to anyone reading it back.
    /// </summary>
    private static string Mhz(double megahertz) =>
        megahertz.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    public static string CallsignFor(Airfield airfield, string? suffix)
    {
        var name = string.IsNullOrWhiteSpace(airfield.DisplayName) ? airfield.Name : airfield.DisplayName;
        name = name.Trim();

        var hyphen = name.IndexOf('-');
        if (hyphen > 2) name = name[..hyphen].TrimEnd();

        var tail = string.IsNullOrWhiteSpace(suffix) ? "" : " " + suffix.Trim();
        return (name + tail).Trim();
    }

    /// <summary>
    /// Whether this radio looks like one <see cref="ToRadios"/> produced, so a map change can replace
    /// the towers instead of adding a second set beside them.
    ///
    /// Recognised by the whole fingerprint rather than any single trait: the callsign ends with the
    /// configured suffix, it answers airfield requests, it answers nothing else, and it has no wake
    /// word of its own. A hand-built radio matching all four is indistinguishable from a generated
    /// one and will be treated as generated - which is why the caller shows what it is about to
    /// remove and why nothing is saved until the operator says so.
    /// </summary>
    /// <remarks>
    /// With an EMPTY callsign suffix this returns false for everything. There is no fingerprint left
    /// worth trusting at that point, and guessing would mean deleting somebody's configuration on a
    /// hunch. See <see cref="GeneratedRadios"/>, which says so out loud instead.
    /// </remarks>
    public static bool LooksGenerated(RadioConfig radio, AppConfig config)
    {
        var suffix = config.TowerPlanCallsignSuffix?.Trim() ?? "";
        if (suffix.Length == 0) return false;

        return radio.Callsign.Trim().EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               && radio.AnswerAirfieldRequests == true
               && radio.AnswerTacticalRequests == false
               && radio.AnswerFriendlyPositionRequests == false
               && string.IsNullOrWhiteSpace(radio.Keyword);
    }

    /// <param name="Generated">The radios that match the fingerprint, in the order they appear.</param>
    /// <param name="Notes">What was found, or why nothing could be decided.</param>
    public sealed record GeneratedSet(List<RadioConfig> Generated, List<string> Notes);

    /// <summary>
    /// Picks out the radios that look generated, so they can be removed before regenerating for a
    /// different map. Decides nothing on its own - it only reports.
    /// </summary>
    public static GeneratedSet GeneratedRadios(IEnumerable<RadioConfig> radios, AppConfig config)
    {
        var notes = new List<string>();
        var suffix = config.TowerPlanCallsignSuffix?.Trim() ?? "";

        if (suffix.Length == 0)
        {
            notes.Add("The callsign suffix is empty, so a generated tower cannot be told apart from a " +
                      "hand-built one. Remove the towers you no longer want by hand, or set a suffix first.");
            return new GeneratedSet(new List<RadioConfig>(), notes);
        }

        var generated = radios.Where(r => LooksGenerated(r, config)).ToList();

        notes.Add(generated.Count == 0
            ? $"No radio looks like a generated tower (callsign ending in \"{suffix}\", airfield requests only, no own wake word)."
            : $"{generated.Count} radio(s) look generated: " +
              string.Join(", ", generated.Select(r => $"{r.Callsign} {Mhz(r.FrequencyHz / 1_000_000.0)}")));

        return new GeneratedSet(generated, notes);
    }

    /// <summary>
    /// The plan as radio entries, ready to be put into <see cref="AppConfig.Radios"/>.
    /// </summary>
    /// <remarks>
    /// Each one answers airfield requests and nothing else: a tower that also answered bogey dopes
    /// would make the whole point of splitting the roles pointless, and a dozen radios all serving
    /// tactical requests multiplies the mission-data queries for no gain. The wake word is left empty
    /// so the radio follows the global one - see the class remarks for why that matters more here
    /// than anywhere else.
    /// </remarks>
    public static List<RadioConfig> ToRadios(Result plan) =>
        plan.Towers.Select(tower => new RadioConfig
        {
            FrequencyHz = tower.FrequencyHz,
            Modulation = tower.Modulation,
            Callsign = tower.Callsign,
            Keyword = "",
            AnswerAirfieldRequests = true,
            AnswerTacticalRequests = false,
            AnswerFriendlyPositionRequests = false
        }).ToList();
}
