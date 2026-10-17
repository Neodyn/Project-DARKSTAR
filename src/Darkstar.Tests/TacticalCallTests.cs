using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The declare, spike, formation and fill-in logic.
///
/// All of it is arithmetic and word choice over numbers, which is why it is worth having here: a
/// bearing read one digit wrong, or a cone that does not wrap past north, produces a sentence that
/// sounds entirely correct and sends the pilot to the wrong piece of sky. That class of mistake is
/// invisible in flight and obvious in a table of inputs.
/// </summary>
internal static class TacticalCallTests
{
    public static void Run()
    {
        Section("Reading a declared point out of what the pilot actually said");

        // The reason this file has its own number parser. PilotNames.NormalizeSpokenNumbers maps
        // "for" to 4 on purpose - it exists to match spoken callsigns, where the bet pays off -
        // and a declare call uses "for" as the joiner between bearing and range. Run through the
        // callsign normaliser, "zero three zero for four five" becomes "0 3 0 4 4 5" and the range
        // comes back 445. The call would still answer, confidently, about the wrong place.
        var spoken = TacticalCalls.ParseDeclarePoint("declare bullseye zero three zero for four five");
        Check("\"zero three zero for four five\" is 030 at 45, not 445",
            spoken != null && spoken.BearingDegrees == 30 && Math.Abs(spoken.RangeNm - 45) < 0.001,
            spoken == null ? "no match" : $"{spoken.BearingDegrees} / {spoken.RangeNm}");

        Check("the joiner does not glue the two numbers together either",
            TacticalCalls.NumbersForGeometry("zero three zero for four five").Replace(" ", "") != "030045",
            TacticalCalls.NumbersForGeometry("zero three zero for four five"));

        var figures = TacticalCalls.ParseDeclarePoint("declare bullseye 030 45");
        Check("figures as the transcriber usually writes them",
            figures != null && figures.BearingDegrees == 30 && Math.Abs(figures.RangeNm - 45) < 0.001,
            figures == null ? "no match" : $"{figures.BearingDegrees} / {figures.RangeNm}");

        var spacedDigits = TacticalCalls.ParseDeclarePoint("declare 0 3 0 for 4 5");
        Check("digits spoken one at a time",
            spacedDigits != null && spacedDigits.BearingDegrees == 30 && Math.Abs(spacedDigits.RangeNm - 45) < 0.001,
            spacedDigits == null ? "no match" : $"{spacedDigits.BearingDegrees} / {spacedDigits.RangeNm}");

        // "Forty five" is how most people say a range out loud, and it is two words that are not
        // two digits.
        var compound = TacticalCalls.ParseDeclarePoint("declare bullseye two seven zero at forty five");
        Check("\"forty five\" is 45, not a 4 and a 5",
            compound != null && compound.BearingDegrees == 270 && Math.Abs(compound.RangeNm - 45) < 0.001,
            compound == null ? "no match" : $"{compound.BearingDegrees} / {compound.RangeNm}");

        var teen = TacticalCalls.ParseDeclarePoint("declare bullseye one eight zero for fifteen");
        Check("\"fifteen\" is 15",
            teen != null && teen.BearingDegrees == 180 && Math.Abs(teen.RangeNm - 15) < 0.001,
            teen == null ? "no match" : $"{teen.BearingDegrees} / {teen.RangeNm}");

        var round = TacticalCalls.ParseDeclarePoint("declare bullseye three six zero for twenty");
        Check("\"twenty\" on its own is 20",
            round != null && Math.Abs(round.RangeNm - 20) < 0.001,
            round == null ? "no match" : $"{round.RangeNm}");

        // North is "three six zero" on the radio and 0 everywhere a bearing is used. Treating 360
        // as out of range - which it is, strictly - would reject the one bearing a pilot is most
        // likely to say out loud.
        Check("\"three six zero\" is north, not out of range",
            round != null && round.BearingDegrees == 0,
            round == null ? "no match" : $"{round.BearingDegrees}");
        Check("361 is still out of range", TacticalCalls.ParseDeclarePoint("declare 361 for 20") == null);
        Check("a spike called on 360 is north too", TacticalCalls.ParseSpikeBearing("spike 360") == 0);

        // "niner" and "tree" are the whole reason the brevity words exist, and they go through the
        // callsign normaliser, which already knows them.
        var brevity = TacticalCalls.ParseDeclarePoint("declare bullseye two niner tree for two zero");
        Check("\"tree\" and \"niner\" are 3 and 9",
            brevity != null && brevity.BearingDegrees == 293 && Math.Abs(brevity.RangeNm - 20) < 0.001,
            brevity == null ? "no match" : $"{brevity.BearingDegrees} / {brevity.RangeNm}");

        Check("a three-digit range survives",
            TacticalCalls.ParseDeclarePoint("declare bullseye 090 for 120")?.RangeNm == 120);

        Section("The bullseye is not the pilot's own nose");

        // Same two numbers, two completely different points. Getting this backwards answers about
        // sky the pilot is not looking at.
        Check("\"bullseye\" is recognised",
            TacticalCalls.ParseDeclarePoint("declare bullseye 030 45")?.FromBullseye == true);
        Check("\"bulls eye\" split in two is still the bullseye",
            TacticalCalls.ParseDeclarePoint("declare bulls eye 030 45")?.FromBullseye == true);
        Check("without the word, the point is from the aircraft",
            TacticalCalls.ParseDeclarePoint("declare 030 45")?.FromBullseye == false);

        Section("A point that cannot exist gets no verdict");

        // Better to ask again than to answer about a place that is not on the map.
        Check("a bearing past 359 is refused", TacticalCalls.ParseDeclarePoint("declare 450 for 20") == null);
        Check("an absurd range is refused", TacticalCalls.ParseDeclarePoint("declare 030 for 900") == null);
        Check("a declare with no numbers is not an error, just no point",
            TacticalCalls.ParseDeclarePoint("declare") == null);
        Check("nothing at all is no point", TacticalCalls.ParseDeclarePoint(null) == null);

        Section("Reading a spike bearing");

        Check("\"spike 270\"", TacticalCalls.ParseSpikeBearing("spike 270") == 270);
        Check("\"spiked two seven zero\"", TacticalCalls.ParseSpikeBearing("spiked two seven zero") == 270);
        Check("\"spike zero niner zero\"", TacticalCalls.ParseSpikeBearing("spike zero niner zero") == 90);
        Check("\"Darkstar, spike 045\" with the callsign in front",
            TacticalCalls.ParseSpikeBearing("Darkstar, spike 045") == 45);

        // The word anchors the number. A sentence that mentions another bearing first must not
        // have that one read as the spike.
        Check("a bearing before the word is not the spike bearing",
            TacticalCalls.ParseSpikeBearing("heading 180, spike 270") == 270,
            $"{TacticalCalls.ParseSpikeBearing("heading 180, spike 270")}");

        Check("a spike with no bearing is null", TacticalCalls.ParseSpikeBearing("spike") == null);
        Check("a sentence without the word is null", TacticalCalls.ParseSpikeBearing("bogey dope") == null);

        Section("The spike cone wraps past north");

        // Where a plain subtraction fails. A spike called on 355 and a contact at 005 are ten
        // degrees apart, not three hundred and fifty.
        Check("355 called, contact at 005 is on it", TacticalCalls.IsOnBearing(355, 5));
        Check("005 called, contact at 355 is on it", TacticalCalls.IsOnBearing(5, 355));
        Check("000 called, contact at 014 is on it", TacticalCalls.IsOnBearing(0, 14));
        Check("000 called, contact at 346 is on it", TacticalCalls.IsOnBearing(0, 346));
        Check("270 called, contact at 090 is not on it", !TacticalCalls.IsOnBearing(270, 90));
        Check("the cone has an edge", !TacticalCalls.IsOnBearing(180, 200));
        Check("and the edge is where it is documented to be",
            TacticalCalls.IsOnBearing(180, 180 + (int)TacticalCalls.SpikeConeDegrees));

        Section("Declare verdicts");

        Check("hostiles only is hostile",
            TacticalCalls.Declare(true, false) == TacticalCalls.DeclareVerdict.Hostile);
        Check("friendlies only is friendly",
            TacticalCalls.Declare(false, true) == TacticalCalls.DeclareVerdict.Friendly);
        Check("nothing there is clean",
            TacticalCalls.Declare(false, false) == TacticalCalls.DeclareVerdict.Clean);

        // The one verdict that changes what the pilot does. Reporting a furball as "hostile"
        // because the hostile was nearer is how a friendly gets shot.
        Check("both sides together is a furball, never hostile",
            TacticalCalls.Declare(true, true) == TacticalCalls.DeclareVerdict.Furball);

        Section("Two groups: azimuth or range");

        // Side by side across the line of sight, 40 miles out: 20 degrees apart is about 14 miles
        // of arc, against 2 miles of range separation.
        Check("abreast is azimuth",
            TacticalCalls.Formation(new[] { 350, 10 }, new[] { 40.0, 42.0 }) == "azimuth",
            TacticalCalls.Formation(new[] { 350, 10 }, new[] { 40.0, 42.0 }) ?? "null");

        // One behind the other on nearly the same bearing.
        Check("in trail is range",
            TacticalCalls.Formation(new[] { 90, 92 }, new[] { 20.0, 40.0 }) == "range",
            TacticalCalls.Formation(new[] { 90, 92 }, new[] { 20.0, 40.0 }) ?? "null");

        // The reason the across figure is an arc and not an angle: the same 20 degrees means 14
        // miles of separation at 40 NM and under 2 at 5 NM. An angle-only test would call both of
        // these the same thing, and one of them would be wrong.
        Check("20 degrees at 40 miles is azimuth",
            TacticalCalls.Formation(new[] { 80, 100 }, new[] { 40.0, 44.0 }) == "azimuth");
        Check("the same 20 degrees at 5 miles, with 4 miles of trail, is range",
            TacticalCalls.Formation(new[] { 80, 100 }, new[] { 5.0, 9.0 }) == "range",
            TacticalCalls.Formation(new[] { 80, 100 }, new[] { 5.0, 9.0 }) ?? "null");

        Section("Three groups: wall, ladder, vic, champagne");

        Check("all abreast is a wall",
            TacticalCalls.Formation(new[] { 70, 90, 110 }, new[] { 40.0, 40.0, 40.0 }) == "wall",
            TacticalCalls.Formation(new[] { 70, 90, 110 }, new[] { 40.0, 40.0, 40.0 }) ?? "null");

        Check("all in trail is a ladder",
            TacticalCalls.Formation(new[] { 90, 90, 90 }, new[] { 20.0, 40.0, 60.0 }) == "ladder",
            TacticalCalls.Formation(new[] { 90, 90, 90 }, new[] { 20.0, 40.0, 60.0 }) ?? "null");

        Check("one group leading two is a vic",
            TacticalCalls.Formation(new[] { 90, 80, 100 }, new[] { 20.0, 40.0, 40.0 }) == "vic",
            TacticalCalls.Formation(new[] { 90, 80, 100 }, new[] { 20.0, 40.0, 40.0 }) ?? "null");

        Check("two groups leading one is champagne",
            TacticalCalls.Formation(new[] { 80, 100, 90 }, new[] { 20.0, 20.0, 40.0 }) == "champagne",
            TacticalCalls.Formation(new[] { 80, 100, 90 }, new[] { 20.0, 20.0, 40.0 }) ?? "null");

        // A wall that straddles north. The spread has to come from the widest pair of bearings:
        // reading the smallest and largest number instead makes 350 and 010 look 10 degrees apart
        // when they are 20, which halves the spread.
        //
        // The ranges here are deliberately not identical. With all three at the same range there
        // is no spread along the line of sight to compare against, so the call comes out "wall"
        // whatever the across figure is - a test that cannot tell the two apart. Four miles of
        // trail is what makes the halved spread change the answer, from wall to vic.
        Check("a wall across north is still a wall",
            TacticalCalls.Formation(new[] { 350, 0, 10 }, new[] { 38.0, 40.0, 42.0 }) == "wall",
            TacticalCalls.Formation(new[] { 350, 0, 10 }, new[] { 38.0, 40.0, 42.0 }) ?? "null");

        Check("and a flat wall across north too",
            TacticalCalls.Formation(new[] { 350, 0, 10 }, new[] { 40.0, 40.0, 40.0 }) == "wall",
            TacticalCalls.Formation(new[] { 350, 0, 10 }, new[] { 40.0, 40.0, 40.0 }) ?? "null");

        Section("Formations only exist for two or three groups");

        Check("one group has no formation", TacticalCalls.Formation(new[] { 90 }, new[] { 40.0 }) == null);
        Check("no groups has no formation", TacticalCalls.Formation(Array.Empty<int>(), Array.Empty<double>()) == null);
        Check("four groups are not called as a shape",
            TacticalCalls.Formation(new[] { 70, 80, 90, 100 }, new[] { 40.0, 40.0, 40.0, 40.0 }) == null);
        Check("mismatched lists are refused rather than guessed",
            TacticalCalls.Formation(new[] { 70, 90 }, new[] { 40.0 }) == null);

        Section("Fill-ins, in a controller's order");

        var single = TacticalCalls.FillIns(1, 350, 20_000, 19_000, "Flanker");
        Check("a single contact is called single", single[0] == "single", string.Join(", ", single));
        Check("the type comes last", single[^1] == "Flanker", string.Join(", ", single));
        Check("an unremarkable group gets no speed or height word", single.Count == 2, string.Join(", ", single));

        Check("two contacts", TacticalCalls.FillIns(2, 350, 20_000, 19_000, "")[0] == "two contacts");

        var heavy = TacticalCalls.FillIns(4, 350, 20_000, 19_000, "");
        Check("four contacts is heavy, with the number", heavy[0] == "heavy, 4 contacts", string.Join(", ", heavy));

        var fast = TacticalCalls.FillIns(2, 620, 25_000, 24_000, "MiG-29");
        Check("600 knots is fast", fast.Contains("fast"), string.Join(", ", fast));
        Check("size, then speed, then type",
            fast.IndexOf("two contacts") < fast.IndexOf("fast") && fast.IndexOf("fast") < fast.IndexOf("MiG-29"),
            string.Join(", ", fast));

        Check("not everything quick is very fast", !TacticalCalls.FillIns(1, 620, 20_000, 19_000, "").Contains("very fast"));
        Check("900 knots is very fast", TacticalCalls.FillIns(1, 950, 20_000, 19_000, "").Contains("very fast"));

        Check("above 40,000 feet is high",
            TacticalCalls.FillIns(1, 400, 43_000, 42_000, "").Contains("high"));
        Check("under 1,000 feet above the ground is low",
            TacticalCalls.FillIns(1, 400, 5_000, 400, "").Contains("low"));

        // A contact in a mountain valley can be high above the sea and still be down in the dirt,
        // which is what the pilot needs to hear. Height above ground decides "low", altitude
        // decides "high", and the two are not the same number.
        var inTheValley = TacticalCalls.FillIns(1, 400, 9_000, 300, "Hind");
        Check("high above the sea but low over the ground is low",
            inTheValley.Contains("low") && !inTheValley.Contains("high"), string.Join(", ", inTheValley));

        Check("an empty type is left out entirely",
            !TacticalCalls.FillIns(1, 400, 20_000, 19_000, "").Any(string.IsNullOrWhiteSpace));
        Check("a null type is left out entirely",
            TacticalCalls.FillIns(1, 400, 20_000, 19_000, null).Count == 1);

        Section("Turning a bullseye call into a place on the map");

        // The inverse of the bearing and distance helpers, and the step that makes a declare
        // possible at all. Checked by going out and coming back: whatever PointFrom produces has
        // to measure as the bearing and range that went in.
        var (lat, lon) = DcsIntelService.PointFrom(42.0, 42.0, 30, 45);
        var backBearing = DcsIntelService.TrueBearing(42.0, 42.0, lat, lon);
        var backRange = DcsIntelService.DistanceNm(42.0, 42.0, lat, lon);
        Check("030 for 45 comes back as 030", Math.Abs(backBearing - 30) < 0.5, $"{backBearing:0.00}");
        Check("and as 45 miles", Math.Abs(backRange - 45) < 0.1, $"{backRange:0.000}");

        // Due north and due south are where a sign error hides: both move latitude only, in
        // opposite directions, and a formula that drops the sign looks right on one of them.
        var north = DcsIntelService.PointFrom(42.0, 42.0, 0, 60);
        var south = DcsIntelService.PointFrom(42.0, 42.0, 180, 60);
        Check("due north increases latitude", north.Lat > 42.0, $"{north.Lat:0.000}");
        Check("due south decreases it", south.Lat < 42.0, $"{south.Lat:0.000}");
        Check("and neither moves longitude",
            Math.Abs(north.Lon - 42.0) < 0.001 && Math.Abs(south.Lon - 42.0) < 0.001,
            $"{north.Lon:0.0000} / {south.Lon:0.0000}");

        var east = DcsIntelService.PointFrom(42.0, 42.0, 90, 60);
        Check("due east increases longitude", east.Lon > 42.0, $"{east.Lon:0.000}");

        // A bearing of 360 is north, not an error, and 420 is 60. The helper normalises rather
        // than trusting its caller, because the callers are a parser and a declination sum.
        var threeSixty = DcsIntelService.PointFrom(42.0, 42.0, 360, 60);
        Check("360 behaves as 0",
            Math.Abs(threeSixty.Lat - north.Lat) < 0.000001 && Math.Abs(threeSixty.Lon - north.Lon) < 0.000001);

        // Nobody flies the Caucasus across the date line, but Marianas and Kola are real maps and
        // the formula is the same one. A longitude of 181 would put a declare in the wrong ocean.
        var acrossTheLine = DcsIntelService.PointFrom(20.0, 179.5, 90, 120);
        Check("a point across the date line stays inside -180..180",
            acrossTheLine.Lon is >= -180 and <= 180, $"{acrossTheLine.Lon:0.000}");

        Section("Which request a transmission is");

        var intel = new DcsIntelService(new AppConfig { DcsIntelEnabled = true, DcsGrpcEnabled = true });

        Check("a declare is a declare",
            intel.Classify("Darkstar, declare bullseye zero three zero for four five") == IntelRequestKind.Declare);
        Check("a spike is a spike",
            intel.Classify("Darkstar, spike two seven zero") == IntelRequestKind.Spike);
        Check("\"nails\" is a spike too",
            intel.Classify("Darkstar, nails two seven zero") == IntelRequestKind.Spike);

        // Both of these name a specific piece of sky, which makes them the more specific question.
        // A declare that also contains the word "bullseye" must not be read as a bogey dope asked
        // for in bullseye format.
        Check("a declare is not mistaken for a bullseye bogey dope",
            intel.Classify("Darkstar, declare bullseye zero three zero for four five") != IntelRequestKind.BogeyDope);

        Check("a bogey dope is still a bogey dope",
            intel.Classify("Darkstar, bogey dope") == IntelRequestKind.BogeyDope);
        Check("a picture is still a picture",
            intel.Classify("Darkstar, picture") == IntelRequestKind.Picture);
    }
}
