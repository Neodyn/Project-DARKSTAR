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

        var slices = RadioWheel.Slices;
        Eq("exactly the F10 menu's calls, in the catalogue's order",
            string.Join(", ", slices.Select(c => c.Id)),
            "bogey_dope, threat_check, picture, alpha_check, runway_in_use, atis, divert, radio_check");
        Check("eight of them, so 1-8 reach every slice and a hat has a direction for each", slices.Count == 8,
            $"{slices.Count}");

        Section("What a pick types");

        var settings = new WheelSettings();
        var picture = slices.First(c => c.Kind == CallKind.Picture);
        Eq("marker, callsign, colon, the menu label", RadioWheel.ChatLine(settings, picture), "» Overlord: Picture");

        // Every slice must come out of the other end as the call it was: the server hook strips the
        // marker and the callsign, and ChatBridge matches what is left.
        var lost = slices
            .Where(call =>
            {
                var line = RadioWheel.ChatLine(settings, call);
                var text = line[(line.IndexOf(':') + 1)..];
                return ChatBridge.MatchCall(text)?.Id != call.Id;
            })
            .Select(call => call.Id)
            .ToList();
        Check("every slice's line is understood as that call by the bot", lost.Count == 0, string.Join(", ", lost));

        Check("the marker is one the server hook accepts",
            ReadSource("dcs/Scripts/Hooks/" + ChatBridge.HookFileName).Contains("'\\194\\187'", StringComparison.Ordinal)
            && settings.Marker == "»");

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

            File.WriteAllText(path, "{ \"Callsign\": \"  \", \"Marker\": \"\", \"JoystickId\": 40, \"JoystickButton\": 99, \"Size\": 7 }");
            var repaired = WheelSettings.Load(path, out _);
            Check("hand-edited nonsense is repaired rather than used",
                repaired.Callsign == "Overlord" && repaired.Marker == "»" && repaired.JoystickId == -1
                && repaired.JoystickButton == 0 && repaired.Size <= 0.95,
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
            window.Contains("i * sweep - 90", StringComparison.Ordinal));
    }
}
