using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The catalogue of calls and the order they are offered in.
///
/// Worth testing on its own precisely because it is the piece that decides what a pilot sees in
/// the F10 menu without anybody flying: it is a pure function of a context record, so every rule
/// in it can be stated as "these inputs, that first entry" and checked with numbers.
/// </summary>
internal static class CallCatalogTests
{
    public static void Run()
    {
        Section("Every call has an identity that survives a round trip through DCS");

        // The id is what travels in the F10 command's details struct and comes back in the event.
        // Two calls sharing one id would route both menu entries to whichever matched first, and
        // the symptom - one entry quietly doing another's job - looks nothing like its cause.
        var ids = CallCatalog.All.Select(c => c.Id).ToList();
        Check("no two calls share an id", ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Count,
            string.Join(", ", ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)));

        Check("no two calls share a kind",
            CallCatalog.All.Select(c => c.Kind).Distinct().Count() == CallCatalog.All.Count);

        // Ids go through a Lua struct and come back. Anything exotic in them is a problem nobody
        // would think to look for, so they are held to the shape they have now.
        var oddIds = CallCatalog.All
            .Where(c => !System.Text.RegularExpressions.Regex.IsMatch(c.Id, "^[a-z][a-z0-9_]*$"))
            .Select(c => c.Id)
            .ToList();
        Check("every id is lower-case, no spaces", oddIds.Count == 0, string.Join(", ", oddIds));

        Check("every call has a menu label", CallCatalog.All.All(c => !string.IsNullOrWhiteSpace(c.MenuLabel)));

        // DCS truncates long menu entries, and a truncated label is a call a pilot cannot identify.
        var longLabels = CallCatalog.All.Where(c => c.MenuLabel.Length > 20).Select(c => c.MenuLabel).ToList();
        Check("no menu label runs past 20 characters", longLabels.Count == 0, string.Join(", ", longLabels));

        Check("every call shows how to say it instead",
            CallCatalog.All.All(c => !string.IsNullOrWhiteSpace(c.SpokenExample)));

        Check("a known id resolves", CallCatalog.ById("bogey_dope")?.Kind == CallKind.BogeyDope);
        Check("an id resolves whatever its case", CallCatalog.ById("BOGEY_DOPE")?.Kind == CallKind.BogeyDope);
        Check("an unknown id resolves to nothing", CallCatalog.ById("drop_tanks") == null);
        Check("an empty id resolves to nothing", CallCatalog.ById("") == null && CallCatalog.ById(null) == null);

        Section("A radio only offers what its role allows");

        var everythingOff = new CallContext();
        var offeredWithNothingOn = CallCatalog.Available(everythingOff);

        // The radio check is the one call whose entire purpose is working when nothing else does,
        // so it survives a radio with every feature switched off - and on such a radio it is the
        // only thing in the MENU. "Say again" survives too, because repeating the last reply needs
        // no feature at all, but it is a spoken call and never a menu entry.
        var menuWithNothingOn = offeredWithNothingOn.Where(CallCatalog.MenuOffers).ToList();
        Check("a radio with no roles still offers the radio check",
            menuWithNothingOn.Count == 1 && menuWithNothingOn[0].Kind == CallKind.RadioCheck,
            string.Join(", ", menuWithNothingOn.Select(c => c.Id)));

        Check("and \"say again\" works on it too, as a spoken call",
            offeredWithNothingOn.Any(c => c.Kind == CallKind.SayAgain),
            string.Join(", ", offeredWithNothingOn.Select(c => c.Id)));

        var tacticalOnly = new CallContext { TacticalAvailable = true };
        Check("a tactical radio offers no airfield calls",
            CallCatalog.Available(tacticalOnly).All(c => c.Capability != RadioCapability.Airfield));

        var airfieldOnly = new CallContext { AirfieldAvailable = true };
        Check("an airfield radio offers no tactical calls",
            CallCatalog.Available(airfieldOnly).All(c => c.Capability != RadioCapability.Tactical));
        Check("an airfield radio offers the runway, the ATIS and the divert",
            CallCatalog.Available(airfieldOnly).Count(c => c.Capability == RadioCapability.Airfield) == 3,
            string.Join(", ", CallCatalog.Available(airfieldOnly)
                .Where(c => c.Capability == RadioCapability.Airfield).Select(c => c.Id)));

        // A friendly position needs a pilot's name, and a menu entry carries no argument. It has to
        // stay out rather than appear and then ask "which aircraft?" over a shared frequency.
        Check("no menu entry needs an argument it cannot carry",
            CallCatalog.All.All(c => c.Capability != RadioCapability.FriendlyPosition));

        Section("The threat circle is offered in the one direction that can work");

        var circleOff = new CallContext { TacticalAvailable = true, ThreatCircleSupported = false, ThreatCircleActive = false };
        Check("with threat circles switched off, neither entry appears",
            CallCatalog.Available(circleOff).All(c =>
                c.Kind != CallKind.ThreatCircleStart && c.Kind != CallKind.ThreatCircleCancel));

        var noCircleYet = new CallContext { TacticalAvailable = true, ThreatCircleSupported = true };
        var withCircle = noCircleYet with { ThreatCircleActive = true };

        Check("with no circle running, only 'on' is offered",
            CallCatalog.Available(noCircleYet).Any(c => c.Kind == CallKind.ThreatCircleStart) &&
            CallCatalog.Available(noCircleYet).All(c => c.Kind != CallKind.ThreatCircleCancel));

        Check("with a circle running, only 'off' is offered",
            CallCatalog.Available(withCircle).Any(c => c.Kind == CallKind.ThreatCircleCancel) &&
            CallCatalog.Available(withCircle).All(c => c.Kind != CallKind.ThreatCircleStart));

        Section("What sits on F1 follows the situation rather than a fixed list");

        // This is the menu's answer to "the suggested next call". Each case states the situation and
        // the one entry a pilot would reach for in it.
        static CallKind First(CallContext context) => CallCatalog.Available(context).First().Kind;

        var onTheRamp = new CallContext { TacticalAvailable = true, AirfieldAvailable = true, NearAirfield = true };
        Check("on the ramp, F1 is the runway in use", First(onTheRamp) == CallKind.RunwayInUse,
            First(onTheRamp).ToString());

        var airborneQuiet = new CallContext { TacticalAvailable = true, AirfieldAvailable = true };
        Check("airborne with a clean picture, F1 is the bogey dope", First(airborneQuiet) == CallKind.BogeyDope,
            First(airborneQuiet).ToString());

        var airborneThreat = airborneQuiet with { HostilesNearby = true };
        Check("airborne with hostiles about, F1 is still the bogey dope",
            First(airborneThreat) == CallKind.BogeyDope, First(airborneThreat).ToString());

        // The case the ordering exists for: close to a field, the airfield calls have to beat the
        // tactical ones even though hostiles are in range, or a pilot taxiing gets a BRAA call on F1.
        var rampWithThreat = onTheRamp with { HostilesNearby = true };
        Check("on the ramp with hostiles about, F1 is still the runway",
            First(rampWithThreat) == CallKind.RunwayInUse, First(rampWithThreat).ToString());

        // Cancelling has to be reachable, but a live threat call still outranks it.
        var airborneWithCircle = airborneThreat with { ThreatCircleSupported = true, ThreatCircleActive = true };
        var orderedWithCircle = CallCatalog.Available(airborneWithCircle);
        Check("cancelling a circle sits above the quiet calls",
            orderedWithCircle.FindIndex(c => c.Kind == CallKind.ThreatCircleCancel) <
            orderedWithCircle.FindIndex(c => c.Kind == CallKind.Picture));
        Check("but never above a live threat call",
            orderedWithCircle.FindIndex(c => c.Kind == CallKind.BogeyDope) <
            orderedWithCircle.FindIndex(c => c.Kind == CallKind.ThreatCircleCancel));

        // The radio check is the call you make when you think nothing works. It must never be the
        // thing a pilot has to scroll past to reach a real call.
        foreach (var (name, context) in new (string, CallContext)[]
                 {
                     ("on the ramp", onTheRamp),
                     ("airborne", airborneQuiet),
                     ("airborne with a circle", airborneWithCircle),
                 })
        {
            var ordered = CallCatalog.Available(context);
            Check($"the radio check is last {name}", ordered[^1].Kind == CallKind.RadioCheck,
                ordered[^1].Kind.ToString());
        }

        Section("The menu does not reshuffle under the pilot's thumb");

        // Same situation twice must give the same menu, or a pilot who has learnt that F1 is the
        // bogey dope finds a picture call there on the next sweep. Guaranteed by the stable sort,
        // which is exactly the sort of property that survives until somebody "tidies" the ordering.
        var twice = new CallContext
        {
            TacticalAvailable = true,
            AirfieldAvailable = true,
            ThreatCircleSupported = true,
            HostilesNearby = true,
        };
        Check("the same situation gives the same order",
            string.Join(",", CallCatalog.Available(twice).Select(c => c.Id)) ==
            string.Join(",", CallCatalog.Available(twice).Select(c => c.Id)));

        // Ties have to fall back on the declared order rather than on whatever the sort felt like.
        var allQuiet = new CallContext { TacticalAvailable = true, AirfieldAvailable = true };
        var declared = CallCatalog.All.Where(c => CallCatalog.Available(allQuiet).Contains(c)).Select(c => c.Id);
        Check("with nothing to prioritise, the declared order is kept",
            string.Join(",", CallCatalog.Available(allQuiet).Select(c => c.Id)) == string.Join(",", declared),
            string.Join(",", CallCatalog.Available(allQuiet).Select(c => c.Id)));

        Section("Calls that carry a number, or per-pilot bookkeeping, stay spoken");

        // A declare carries a point and a spike carries a bearing; neither fits in a menu entry.
        // Check in, check out and say again are kept per pilot under their SRS name, while an F10
        // click identifies a DCS group - the same reason the threat circle is spoken-only.
        foreach (var kind in new[] { CallKind.Declare, CallKind.Spike, CallKind.CheckIn,
                                     CallKind.CheckOut, CallKind.SayAgain })
        {
            var call = CallCatalog.All.First(c => c.Kind == kind);
            Check($"{call.Id} is not a menu entry", !CallCatalog.MenuOffers(call));
            Check($"{call.Id} is still in the catalogue with an example to say",
                !string.IsNullOrWhiteSpace(call.SpokenExample));
        }

        // The divert needs nothing said out loud, so it does belong in the menu - it is the one
        // of the new calls a pilot can click.
        Check("the divert is a menu entry",
            CallCatalog.MenuOffers(CallCatalog.All.First(c => c.Kind == CallKind.Divert)));

        Section("The call id survives the round trip through DCS");

        // The id goes out in a command's details struct and comes back in the event. If that round
        // trip is lossy the symptom is a menu entry that does nothing at all, with no error
        // anywhere - so it is checked against the real protobuf types rather than assumed.
        foreach (var call in CallCatalog.All)
        {
            var details = DcsMenu.DetailsFor(call.Id);
            Check($"\"{call.Id}\" comes back unchanged",
                DcsMenu.CallIdFromDetails(details) == call.Id,
                DcsMenu.CallIdFromDetails(details) ?? "(nothing)");
        }

        Check("and resolves back to its call",
            CallCatalog.ById(DcsMenu.CallIdFromDetails(DcsMenu.DetailsFor("atis")))?.Kind == CallKind.Atis);

        // Other scripts in a mission can raise group command events of their own. Ours are the ones
        // carrying our key; everything else has to be ignored rather than guessed at, or the bot
        // starts answering another mod's menu.
        Check("details from somebody else are ignored",
            DcsMenu.CallIdFromDetails(new Google.Protobuf.WellKnownTypes.Struct()) == null);
        Check("no details at all are ignored", DcsMenu.CallIdFromDetails(null) == null);

        var wrongType = new Google.Protobuf.WellKnownTypes.Struct();
        wrongType.Fields[DcsMenu.DetailsKey] = Google.Protobuf.WellKnownTypes.Value.ForNumber(42);
        Check("a detail of the wrong type is ignored", DcsMenu.CallIdFromDetails(wrongType) == null);

        Section("A menu is rewritten when its content changed, and not otherwise");

        // Rebuilding costs two calls per entry and blinks the menu shut under whoever has it open,
        // so the signature is what decides. It has to notice a changed order too: a pilot who has
        // learnt that F1 is the bogey dope should see the menu rewritten when that stops being true.
        var ramp = new CallContext { TacticalAvailable = true, AirfieldAvailable = true, NearAirfield = true };
        var airborne = ramp with { NearAirfield = false };

        Check("the same situation plans the same menu",
            DcsMenu.Signature(DcsMenu.Plan(ramp)) == DcsMenu.Signature(DcsMenu.Plan(ramp)));

        Check("taking off changes the signature",
            DcsMenu.Signature(DcsMenu.Plan(ramp)) != DcsMenu.Signature(DcsMenu.Plan(airborne)),
            DcsMenu.Signature(DcsMenu.Plan(airborne)));

        // Same calls, different order - the membership alone must not be what is compared.
        var rampIds = DcsMenu.Plan(ramp).Select(e => e.CallId).OrderBy(i => i);
        var airborneIds = DcsMenu.Plan(airborne).Select(e => e.CallId).OrderBy(i => i);
        Check("even though both offer exactly the same calls",
            string.Join(",", rampIds) == string.Join(",", airborneIds));

        // The threat circle is in the catalogue but deliberately not in the menu, so turning one on
        // must leave the menu alone. Asserted rather than assumed: if it ever started appearing,
        // every pilot with a circle would get their menu rewritten under them on the next sweep,
        // and the entry would route to bookkeeping that cannot find them.
        Check("a threat circle does not appear in the menu",
            DcsMenu.Plan(airborne with { ThreatCircleSupported = true }).All(e =>
                e.CallId != "threat_circle_start" && e.CallId != "threat_circle_cancel"));

        Check("and starting one therefore leaves the menu untouched",
            DcsMenu.Signature(DcsMenu.Plan(airborne with { ThreatCircleSupported = true })) ==
            DcsMenu.Signature(DcsMenu.Plan(airborne with { ThreatCircleSupported = true, ThreatCircleActive = true })));

        // But it is still a call the bot answers, and the manuals still teach it. The catalogue is
        // the one list; the menu is a view of it.
        Check("while staying a call the bot answers",
            CallCatalog.Available(airborne with { ThreatCircleSupported = true })
                .Any(c => c.Kind == CallKind.ThreatCircleStart));

        Check("a planned entry carries both the label and the id",
            DcsMenu.Plan(ramp).All(e => !string.IsNullOrWhiteSpace(e.Label) && CallCatalog.ById(e.CallId) != null));

        // The root entry is configurable, but an empty one would hang the commands off the top level
        // of the F10 menu with nothing to remove them by.
        Check("an empty root name falls back to a usable one",
            new DcsMenuService(new AppConfig { F10MenuRootName = "   " }).RootName == "Darkstar");
        Check("a configured root name is used as given",
            new DcsMenuService(new AppConfig { F10MenuRootName = "ATC" }).RootName == "ATC");
    }
}
