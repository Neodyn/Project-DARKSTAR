using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The radio wheel's logic, without its window: which slices it has, what it types, which slice a
/// hat points at, and the file it hands to the client hook. The window itself cannot be tested
/// here - it needs a desktop - so everything it decides is decided in Darkstar.Core.
/// </summary>
internal static class RadioWheelTests
{
    public static void Run()
    {
        Section("The slices");

        var settings = new WheelSettings();
        var top = RadioWheel.Build(settings);

        Eq("the top level, clockwise from the top",
            string.Join(", ", top.Select(i => i.Label)),
            "Bogey dope, Threat check, Picture, Alpha check, Threat circle, Airfield, Radio check");
        Check("no level has more slices than an eight-way hat has directions, minus one to spare",
            top.Count <= RadioWheel.MaxSlices && top.All(i => (i.Children?.Count ?? 0) <= RadioWheel.MaxSlices));

        var circle = top.Single(i => i.Label == "Threat circle");
        Eq("the threat circle opens its distances, then Off",
            string.Join(", ", circle.Children!.Select(i => i.Label)), "20 NM, 30 NM, 40 NM, 60 NM, 80 NM, Off");
        var airfield = top.Single(i => i.Label == "Airfield");
        Eq("the airfield calls sit one level down",
            string.Join(", ", airfield.Children!.Select(i => i.Label)), "Runway in use, ATIS, Divert");
        Check("a door to a sub-wheel sends nothing itself", circle.ChatText == null && airfield.ChatText == null);

        // Every slice must be reachable by the hat on every level - the reason the airfield calls
        // moved into a sub-wheel when the threat circle arrived.
        foreach (var level in new[] { ("top", top), ("threat circle", circle.Children!), ("airfield", airfield.Children!) })
        {
            var reached = Enumerable.Range(0, 8)
                .Select(d => RadioWheel.SliceForHat(d * 4500, level.Item2.Count))
                .Where(x => x != null)
                .Select(x => x!.Value)
                .Distinct()
                .Count();
            Check($"every slice of the {level.Item1} level has a hat direction", reached == level.Item2.Count,
                $"{reached} of {level.Item2.Count}");
        }

        Section("What a pick types");

        var picture = top.Single(i => i.Label == "Picture");
        Eq("marker, callsign, colon, the menu label", RadioWheel.ChatLine(settings, picture), "» Overlord: Picture");
        Eq("a distance types the threat circle with its radius",
            RadioWheel.ChatLine(settings, circle.Children![2]), "» Overlord: Threat circle 40");

        // Every leaf must come out of the other end as what it was: the server hook strips the
        // marker and the callsign, ChatBridge matches a menu call, and the threat circle goes
        // through the same classifier and radius parser as the spoken call.
        var intel = new DcsIntelService(new AppConfig());
        var leaves = top.SelectMany(i => i.OpensSubWheel ? i.Children! : new[] { i }).ToList();
        Check("there are eight calls and six circle choices to check", leaves.Count == 14, $"{leaves.Count}");

        foreach (var leaf in leaves)
        {
            var line = RadioWheel.ChatLine(settings, leaf);
            var text = line[(line.IndexOf(':') + 1)..].Trim();
            var call = ChatBridge.MatchCall(text);

            if (leaf.Label.EndsWith(" NM", StringComparison.Ordinal))
            {
                var radius = int.Parse(leaf.Label[..^3]);
                Check($"\"{text}\" starts a circle of {radius} NM",
                    call == null && intel.Classify(text) == IntelRequestKind.ThreatCircleStart
                    && Math.Abs(intel.ParseRequestedRadiusNm(text) - radius) < 0.01,
                    $"{intel.Classify(text)}, {intel.ParseRequestedRadiusNm(text)}");
            }
            else if (leaf.Label == "Off")
            {
                Check($"\"{text}\" cancels the circle",
                    call == null && intel.Classify(text) == IntelRequestKind.ThreatCircleCancel, $"{intel.Classify(text)}");
            }
            else
            {
                Check($"\"{text}\" is understood as {leaf.Label}", call?.MenuLabel == leaf.Label, call?.Id ?? "no call");
            }
        }

        Check("the marker is one the server hook accepts",
            ReadSource("dcs/Scripts/Hooks/" + ChatBridge.HookFileName).Contains("'\\194\\187'", StringComparison.Ordinal)
            && settings.Marker == "\u00bb");

        Check("the radii come from the settings",
            string.Join(",", RadioWheel.Build(new WheelSettings { ThreatCircleRadii = new() { 10, 25 } })
                .Single(i => i.Label == "Threat circle").Children!.Select(i => i.Label)) == "10 NM,25 NM,Off");

        Section("The hat");

        void Points(int pov, int? expected) =>
            Eq($"POV {pov}", RadioWheel.SliceForHat(pov, 8)?.ToString() ?? "centred", expected?.ToString() ?? "centred");

        Points(0, 0);          // forward: the top slice
        Points(4500, 1);       // forward-right
        Points(9000, 2);       // right
        Points(18000, 4);      // back: the bottom slice
        Points(27000, 6);      // left
        Points(31500, 7);      // forward-left
        Points(35900, 0);      // just left of forward is still the top
        Points(2000, 0);       // a hat that reads a few degrees off still lands on the slice it means
        Points(65535, null);   // centred
        Points(-1, null);
        Check("no slices, no slice", RadioWheel.SliceForHat(0, 0) == null);

        Section("The file the client hook sends");

        var folder = Path.Combine(Path.GetTempPath(), "darkstar-wheel-" + Guid.NewGuid().ToString("N")[..8], "Out");
        try
        {
            var first = RadioWheel.Write(folder, "» Overlord: Picture");
            var second = RadioWheel.Write(folder, "» Overlord: Bogey dope\r\nsecond line");

            Check("written as .txt", first.EndsWith(".txt", StringComparison.Ordinal) && File.Exists(first));
            Check("no .tmp left behind", Directory.GetFiles(folder, "*.tmp").Length == 0);
            Check("named so that sorting by name is the order of the picks",
                string.CompareOrdinal(Path.GetFileName(first), Path.GetFileName(second)) < 0,
                $"{Path.GetFileName(first)} / {Path.GetFileName(second)}");

            var bytes = File.ReadAllBytes(first);
            Check("UTF-8 without a byte order mark, which Lua would send as part of the line",
                bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
            Eq("one line, as typed", File.ReadAllText(first), "» Overlord: Picture\n");
            Eq("a line break inside a pick cannot become a second chat line",
                File.ReadAllText(second), "» Overlord: Bogey dope  second line\n");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); } catch { /* temp */ }
        }

        Check("the outbox is the folder the client hook watches",
            RadioWheel.OutboxFolder.EndsWith(Path.Combine("Darkstar-Wheel", "Out"), StringComparison.Ordinal));

        var hook = ReadSource("dcs/Scripts/Hooks/Darkstar-Wheel.lua");
        Check("which reads it under the same name", hook.Contains("'\\\\Darkstar-Wheel\\\\Out'", StringComparison.Ordinal));
        Check("from TMP before TEMP, the order Windows and Path.GetTempPath use",
            hook.Contains("os.getenv('TMP') or os.getenv('TEMP')", StringComparison.Ordinal));
        Check("and sends only to the own side", hook.Contains("net.send_chat, line, false", StringComparison.Ordinal));
        Check("and only as a client", hook.Contains("multiplayer == true and server == false", StringComparison.Ordinal));

        Section("The player's settings");

        var temp = Path.Combine(Path.GetTempPath(), "darkstar-wheel-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = Path.Combine(temp, "wheel.json");

            var missing = WheelSettings.Load(path, out var noFile);
            Check("no file gives the defaults, quietly", noFile == null && missing.Callsign == "Overlord" && missing.KeyCode == 0x91);

            var changed = new WheelSettings { Callsign = "Magic", JoystickId = 2, JoystickButton = 5, Size = 0.6 };
            Check("saved", changed.Save(path) == null);
            var back = WheelSettings.Load(path, out var none);
            Check("and read back", none == null && back.Callsign == "Magic" && back.JoystickId == 2 && back.JoystickButton == 5
                && Math.Abs(back.Size - 0.6) < 1e-9);

            File.WriteAllText(path, "{ this is not json");
            var broken = WheelSettings.Load(path, out var problem);
            Check("a broken file still gives a working wheel, and says why",
                problem != null && broken.Callsign == "Overlord", problem ?? "no problem reported");

            File.WriteAllText(path, "{ \"Callsign\": \"  \", \"Marker\": \"\", \"JoystickId\": 40, \"JoystickButton\": 99, \"Size\": 7, \"ThreatCircleRadii\": [0, 500, 40, 40] }");
            var repaired = WheelSettings.Load(path, out _);
            Check("hand-edited nonsense is repaired rather than used",
                repaired.Callsign == "Overlord" && repaired.Marker == "»" && repaired.JoystickId == -1
                && repaired.JoystickButton == 0 && repaired.Size <= 0.95
                && string.Join(",", repaired.ThreatCircleRadii) == "40",
                $"{repaired.Callsign} {repaired.Marker} {repaired.JoystickId} {repaired.JoystickButton} {repaired.Size}");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp */ }
        }

        Section("The wheel program");

        var app = ReadSource("src/Darkstar.Wheel/WheelApp.cs");
        Check("it only reacts while DCS is in front - the key types normally everywhere else",
            app.Contains("if (!_wheel.Visible && !DcsInFront()) return false;", StringComparison.Ordinal));
        Check("the hook delegate is kept in a field, so it is not collected under user32",
            app.Contains("_hookProc = OnKeyboard;", StringComparison.Ordinal));
        Check("a key swallowed on the way down is swallowed on the way up",
            app.Contains("if (up && _swallowed.Remove(vk)) return (IntPtr)1;", StringComparison.Ordinal));
        Check("and it says so when a pick was not picked up",
            app.Contains("was not sent", StringComparison.Ordinal));

        var window = ReadSource("src/Darkstar.Wheel/WheelWindow.cs");
        Check("the wheel never takes the focus or a click from DCS",
            window.Contains("WS_EX_NOACTIVATE", StringComparison.Ordinal) && window.Contains("WS_EX_TRANSPARENT", StringComparison.Ordinal)
            && window.Contains("ShowWithoutActivation => true", StringComparison.Ordinal));
        Check("slice 1 is drawn at the top, where the hat's forward points",
            window.Contains("var mid = i * sweep - 90;", StringComparison.Ordinal));
        Check("a sub-wheel waits for the hat to come back to the centre before it picks",
            app.Contains("_hatWasCentred = false;", StringComparison.Ordinal)
            && app.Contains("if (hatSlice == null) _hatWasCentred = true;", StringComparison.Ordinal));
    }
}
