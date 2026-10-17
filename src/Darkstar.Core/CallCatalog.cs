namespace Darkstar;

/// <summary>
/// Every request a pilot can make, as one list, independent of how the request arrived.
///
/// This exists because a request can now reach the bot two ways - spoken over SRS, or picked from
/// the F10 menu - and a third (a client-side radio wheel) is plausible later. Without one list,
/// each input path grows its own idea of what the bot can do, and they drift: a call added to
/// the voice triggers but never to the menu is a call pilots can't find, and the drift is silent
/// because nothing compares the two.
///
/// So the rule is: this file knows WHAT can be asked and WHICH of those make sense right now. It
/// does not know whether somebody spoke, clicked, or will one day turn a wheel - no transcript, no
/// menu path, no gRPC type appears here. Everything in it is a pure function of a
/// <see cref="CallContext"/>, which is what makes the ordering testable with numbers instead of a
/// running mission.
/// </summary>
public enum CallKind
{
    BogeyDope,
    Picture,
    ThreatCheck,
    ThreatCircleStart,
    ThreatCircleCancel,
    AlphaCheck,
    Declare,
    Spike,
    RunwayInUse,
    Atis,
    Divert,
    CheckIn,
    CheckOut,
    SayAgain,
    RadioCheck,
}

/// <summary>One request the bot can answer.</summary>
/// <param name="Kind">Which request this is.</param>
/// <param name="Id">
/// A stable identifier that survives a round trip through DCS. It travels in the F10 command's
/// <c>details</c> struct and comes back in the event, so the bot never has to parse a menu label
/// back into a meaning - rename the label freely, the id is what is matched.
/// </param>
/// <param name="MenuLabel">What the pilot reads in the F10 menu. Kept short; DCS truncates.</param>
/// <param name="Capability">
/// Which radio job this belongs to, so a menu entry can't offer something the radio plan has
/// switched off. Null means every radio answers it - the radio check, which is the one call whose
/// whole point is working when nothing else does.
/// </param>
/// <param name="SpokenExample">How a pilot would say the same thing, for the manuals and the kneeboard.</param>
/// <param name="EchoPhrase">
/// The request on its own, without any callsign - what the pilot's voice reads back on the
/// frequency after a menu click, so the other pilots hear the question and not only the answer.
/// It is the tail of <see cref="SpokenExample"/> with the example callsigns removed, and a test
/// holds the two together so neither can drift.
/// </param>
public sealed record CallDefinition(
    CallKind Kind,
    string Id,
    string MenuLabel,
    RadioCapability? Capability,
    string SpokenExample,
    string EchoPhrase);

/// <summary>
/// What is true for one pilot at one moment. Everything the ordering needs, and nothing that
/// requires a live connection to find out - the caller assembles this from data it has already
/// fetched, so the catalogue itself stays pure.
/// </summary>
public sealed record CallContext
{
    /// <summary>Tactical replies are on, globally and on the radio in question.</summary>
    public bool TacticalAvailable { get; init; }

    /// <summary>Runway and ATIS replies are on.</summary>
    public bool AirfieldAvailable { get; init; }

    /// <summary>This pilot already has a standing watch, so it can be cancelled and not started again.</summary>
    public bool ThreatCircleActive { get; init; }

    /// <summary>Threat circles are switched on at all.</summary>
    public bool ThreatCircleSupported { get; init; }

    /// <summary>
    /// The pilot is on or very near an airfield. Not "parked": a pilot in the circuit wants the
    /// runway too, and the bot cannot tell a touch-and-go from a taxi without more state than this
    /// is worth.
    /// </summary>
    public bool NearAirfield { get; init; }

    /// <summary>A hostile is within reporting range, so a bogey dope would actually say something.</summary>
    public bool HostilesNearby { get; init; }

}

public static class CallCatalog
{
    /// <summary>
    /// Every call, in the order they are listed when nothing about the situation argues otherwise.
    ///
    /// Two calls are deliberately absent, and both for the same reason - a menu entry carries no
    /// argument:
    ///
    /// - A friendly position needs the name of the pilot being asked about. A submenu of every
    ///   player on the coalition would have to be rebuilt whenever anybody joins or changes slot,
    ///   and on a full server it is a menu nobody can scroll. It stays a spoken call.
    /// - A threat circle. Not for want of an argument - the menu entry would start one at the
    ///   configured radius - but because a circle is bookkeeping held per pilot under their SRS
    ///   name, and an F10 click identifies a DCS group instead. Offering it from the menu means
    ///   keying that bookkeeping both ways, which is a change to the one service a pilot notices
    ///   immediately when it misbehaves. It stays a spoken call until that is done properly.
    /// </summary>
    /// <remarks>
    /// The order here and the base numbers in <see cref="Rank"/> are kept in step on purpose: the
    /// manuals and the kneeboard read this list, the menu reads the ranks, and a pilot who learns
    /// one order and then sees the other has been told two different things. A test holds the two
    /// together.
    /// </remarks>
    public static IReadOnlyList<CallDefinition> All { get; } = new List<CallDefinition>
    {
        new(CallKind.BogeyDope,          "bogey_dope",           "Bogey dope",        RadioCapability.Tactical, "Overlord, bogey dope",                                   "bogey dope"),
        new(CallKind.ThreatCheck,        "threat_check",         "Threat check",      RadioCapability.Tactical, "Overlord, threat check",                                 "threat check"),
        new(CallKind.Spike,              "spike",                "Spike",             RadioCapability.Tactical, "Overlord, spike two seven zero",                         "spike two seven zero"),
        new(CallKind.Picture,            "picture",              "Picture",           RadioCapability.Tactical, "Overlord, picture",                                      "picture"),
        new(CallKind.Declare,            "declare",              "Declare",           RadioCapability.Tactical, "Overlord, declare bullseye zero three zero for four five", "declare bullseye zero three zero for four five"),
        // ^ Both carry a number the pilot has to say, so neither becomes a menu entry - see
        //   MenuOffers. They are here because they ARE calls, and the manuals read this list.
        new(CallKind.AlphaCheck,         "alpha_check",          "Alpha check",       RadioCapability.Tactical, "Overlord, alpha check bullseye",                         "alpha check bullseye"),
        new(CallKind.ThreatCircleStart,  "threat_circle_start",  "Threat circle on",  RadioCapability.Tactical, "Overlord, threat circle forty miles",                    "threat circle forty miles"),
        new(CallKind.ThreatCircleCancel, "threat_circle_cancel", "Threat circle off", RadioCapability.Tactical, "Overlord, cancel threat circle",                         "cancel threat circle"),
        // ^ Both are in the catalogue because they ARE calls a pilot can make; they are kept out of
        //   the menu by MenuOffers below, not by being absent here. The catalogue is the one list.
        new(CallKind.RunwayInUse,        "runway_in_use",        "Runway in use",     RadioCapability.Airfield, "Overlord, runway in use",                                "runway in use"),
        new(CallKind.Atis,               "atis",                 "ATIS",              RadioCapability.Airfield, "Overlord, ATIS",                                         "ATIS"),
        new(CallKind.Divert,             "divert",               "Divert",            RadioCapability.Airfield, "Overlord, vector to nearest",                            "vector to nearest"),
        new(CallKind.CheckIn,            "check_in",             "Check in",          RadioCapability.Tactical, "Overlord, Punch one one checking in",                    "checking in"),
        new(CallKind.CheckOut,           "check_out",            "Check out",         RadioCapability.Tactical, "Overlord, Punch one one checking out",                   "checking out"),
        new(CallKind.SayAgain,           "say_again",            "Say again",         null,                     "Overlord, say again",                                    "say again"),
        new(CallKind.RadioCheck,         "radio_check",          "Radio check",       null,                     "Overlord, radio check",                                  "radio check"),
    };

    /// <summary>
    /// Whether a call can be offered as a menu entry at all, as opposed to only being spoken.
    ///
    /// Separate from <see cref="Available"/> on purpose: the reasons here are about what the F10
    /// menu can express, not about the situation. A spoken-only call is still a call - it stays in
    /// <see cref="All"/>, the manuals still document it, and the kneeboard still teaches it.
    /// </summary>
    public static bool MenuOffers(CallDefinition call) => call.Kind switch
    {
        // Needs the name of another pilot, and a menu entry carries no argument.
        _ when call.Capability == RadioCapability.FriendlyPosition => false,

        // Held per pilot under their SRS name; an F10 click identifies a DCS group. See the note
        // on All - this is a scope line, not a limitation of DCS.
        CallKind.ThreatCircleStart or CallKind.ThreatCircleCancel => false,

        // A declare carries a point and a spike carries a bearing. A submenu of every bearing in
        // thirty-degree steps would be twelve entries to answer a question the pilot could have
        // said in two seconds, and a declare point cannot be expressed in a menu at all.
        CallKind.Declare or CallKind.Spike => false,

        // Keyed per pilot under their SRS name, exactly like the threat circle, and an F10 click
        // identifies a DCS group instead. The menu makes "say again" largely moot anyway: a pilot
        // who missed a reply can click the same entry again, which is better than a repeat.
        CallKind.CheckIn or CallKind.CheckOut or CallKind.SayAgain => false,

        _ => true,
    };

    /// <summary>Looks a call up by the id that travelled through DCS. Null for anything unrecognised.</summary>
    public static CallDefinition? ById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The calls worth offering right now, most useful first.
    ///
    /// The ordering is the menu's answer to "Enter picks the suggested next call": there is no
    /// Enter in an F10 menu, so the suggestion is simply whatever sits on F1. It is derived rather
    /// than remembered - no per-pilot state machine, just what the bot already knows about where
    /// they are and what is around them. A wrong guess costs one extra keypress, which is why this
    /// is allowed to be approximate.
    /// </summary>
    public static List<CallDefinition> Available(CallContext context)
    {
        var offered = All.Where(call => IsOffered(call, context)).ToList();

        // A stable sort, so calls that score the same keep the order in All - the list stays
        // predictable between two sweeps when nothing about the situation changed, and a menu that
        // reshuffles under the pilot's thumb is worse than one that is occasionally wrong.
        return offered
            .OrderBy(call => Rank(call, context))
            .ThenBy(call => offered.IndexOf(call))
            .ToList();
    }

    private static bool IsOffered(CallDefinition call, CallContext context)
    {
        var capable = call.Capability switch
        {
            RadioCapability.Tactical => context.TacticalAvailable,
            RadioCapability.Airfield => context.AirfieldAvailable,
            RadioCapability.FriendlyPosition => false, // see the note on All - no argument in a menu
            null => true,                              // the radio check, on every radio always
            _ => false,
        };

        if (!capable) return false;

        return call.Kind switch
        {
            // Offering "cancel" with nothing to cancel, or "start" over a running circle, are both
            // entries whose only possible outcome is a refusal read out on a shared frequency.
            CallKind.ThreatCircleStart => context.ThreatCircleSupported && !context.ThreatCircleActive,
            CallKind.ThreatCircleCancel => context.ThreatCircleSupported && context.ThreatCircleActive,
            _ => true,
        };
    }

    /// <summary>
    /// Lower sorts earlier. The numbers are spread out so a later rule can slot between two
    /// existing ones without renumbering the rest.
    /// </summary>
    private static int Rank(CallDefinition call, CallContext context) => call.Kind switch
    {
        // On the ground or in the circuit, the airfield calls are the only ones anybody wants, and
        // a bogey dope from the ramp is noise.
        CallKind.RunwayInUse when context.NearAirfield => 10,
        CallKind.Atis when context.NearAirfield => 20,

        // Airborne with something hostile in range: the tactical calls move up, in the order a
        // controller would expect them.
        CallKind.BogeyDope when context.HostilesNearby && !context.NearAirfield => 10,
        CallKind.ThreatCheck when context.HostilesNearby && !context.NearAirfield => 20,

        // An active circle means the pilot set one up deliberately, so switching it off should be
        // reachable without hunting - but never ahead of a live threat call.
        CallKind.ThreatCircleCancel when context.ThreatCircleActive => 30,

        CallKind.BogeyDope => 100,
        CallKind.ThreatCheck => 110,
        CallKind.Picture => 120,
        CallKind.AlphaCheck => 130,
        CallKind.ThreatCircleStart => 140,
        CallKind.ThreatCircleCancel => 150,
        CallKind.Declare => 125,
        CallKind.Spike => 115,
        CallKind.RunwayInUse => 200,
        CallKind.Atis => 210,
        CallKind.Divert => 220,
        CallKind.CheckIn => 300,
        CallKind.CheckOut => 310,

        // Spoken-only calls still get a rank: the ordering is what the manuals and the kneeboard
        // list, and those cover every call, not only the ones with a menu entry.
        CallKind.SayAgain => 850,

        // Last on purpose. It is the call you make when you suspect nothing works, not one you
        // reach for in the middle of a fight, and it would otherwise sit above real calls on a
        // radio where everything else is switched off.
        CallKind.RadioCheck => 900,

        _ => 500,
    };
}
