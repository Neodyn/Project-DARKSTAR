using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Darkstar;

/// <summary>
/// The player's settings for the radio wheel (<c>Darkstar.Wheel.exe</c>), kept in
/// <c>%LOCALAPPDATA%\Darkstar\wheel.json</c> on the PLAYER's PC. Nothing here concerns the server:
/// the wheel only types a line into the DCS chat, and the server decides what to do with it.
/// </summary>
public sealed class WheelSettings
{
    /// <summary>Which of the bot's callsigns the wheel addresses. The server answers on the radio
    /// that serves the call, so one callsign covers tower calls and AWACS calls alike.</summary>
    public string Callsign { get; set; } = "Overlord";

    /// <summary>What the line starts with. The server hook accepts "»", "&gt;&gt;" and "@".</summary>
    public string Marker { get; set; } = "»";

    /// <summary>The key that opens and closes the wheel, as a Windows virtual-key code. 0 = no key.
    /// The default is Scroll Lock (0x91), which DCS does not use.</summary>
    public int KeyCode { get; set; } = 0x91;

    /// <summary>The key's name, for the tray menu and the wheel. Written when the key is learnt.</summary>
    public string KeyName { get; set; } = "Scroll Lock";

    /// <summary>Which joystick the button and hat belong to (0-15, as Windows numbers them). -1 = none.</summary>
    public int JoystickId { get; set; } = -1;

    /// <summary>The joystick button that opens the wheel and confirms a slice, counted from 1. 0 = none.</summary>
    public int JoystickButton { get; set; }

    /// <summary>Whether the first hat of that joystick picks a slice while the wheel is open.</summary>
    public bool UseHat { get; set; } = true;

    /// <summary>The wheel's diameter as a share of the DCS window's height.</summary>
    public double Size { get; set; } = 0.5;

    /// <summary>
    /// The radii the threat circle's sub-wheel offers, in nautical miles. At most six, so that
    /// together with "Off" every one of them still has a hat direction of its own.
    /// </summary>
    public List<int> ThreatCircleRadii { get; set; } = new() { 20, 30, 40, 60, 80 };

    /// <summary>The radii offered when the file names none that can be used.</summary>
    internal static readonly int[] DefaultRadii = { 20, 30, 40, 60, 80 };

    /// <summary>Where the settings live on this PC.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Darkstar", "wheel.json");

    // Case-insensitive on reading: the file is meant to be edited by hand, and "callsign" is what
    // somebody types.
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the settings. A missing file gives the defaults; a broken one gives the defaults too,
    /// and <paramref name="problem"/> says why - the wheel must still open, a player in a cockpit
    /// cannot fix a JSON file.
    /// </summary>
    public static WheelSettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return new WheelSettings();

        try
        {
            var loaded = JsonSerializer.Deserialize<WheelSettings>(File.ReadAllText(path), Json);
            if (loaded == null)
            {
                problem = $"{path} is empty - using the defaults.";
                return new WheelSettings();
            }

            loaded.Normalise();
            return loaded;
        }
        catch (Exception ex)
        {
            problem = $"{path} could not be read ({ex.Message}) - using the defaults.";
            return new WheelSettings();
        }
    }

    /// <summary>Writes the settings. Null on success, otherwise what went wrong.</summary>
    public string? Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Repairs values a hand edit can break, instead of failing on them later.</summary>
    internal void Normalise()
    {
        Callsign = string.IsNullOrWhiteSpace(Callsign) ? "Overlord" : Callsign.Trim();
        Marker = string.IsNullOrWhiteSpace(Marker) ? "»" : Marker.Trim();
        KeyName ??= "";
        if (JoystickId is < -1 or > 15) JoystickId = -1;
        if (JoystickButton is < 0 or > 32) JoystickButton = 0;
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 0.25, 0.95) : 0.5;

        // 1-150: the bot clamps to its own maximum anyway, but a 0 or a 4000 is a typing mistake.
        var radii = (ThreatCircleRadii ?? new List<int>())
            .Where(r => r is >= 1 and <= 150)
            .Distinct()
            .OrderBy(r => r)
            .Take(6)
            .ToList();
        ThreatCircleRadii = radii.Count > 0 ? radii : DefaultRadii.ToList();
    }
}

/// <summary>Which job a slice belongs to - the wheel colours its slices and its rim by it.</summary>
public enum WheelGroup
{
    Tactical,
    Airfield,
    Radio,
}

/// <summary>The little picture on a slice. Drawn by the window from lines and circles, no image files.</summary>
public enum WheelIcon
{
    None,
    Crosshair,
    Shield,
    Radar,
    Pin,
    ThreatCircle,
    Tower,
    Runway,
    Windsock,
    Turn,
    Radio,
    Off,
}

/// <summary>One slice of the wheel: a request, or a door to a sub-wheel.</summary>
/// <param name="Label">What the slice says: "Picture", "40 NM", "Airfield".</param>
/// <param name="Group">Which job it belongs to, for its colour.</param>
/// <param name="Icon">Its picture; <see cref="WheelIcon.None"/> shows the label large instead.</param>
/// <param name="ChatText">What follows "Overlord:" in the chat. Null for a door to a sub-wheel.</param>
/// <param name="Spoken">What the pilot voice reads back on the frequency, for the preview in the hub.</param>
/// <param name="Children">The sub-wheel this slice opens, if it is a door.</param>
public sealed record WheelItem(
    string Label,
    WheelGroup Group,
    WheelIcon Icon,
    string? ChatText = null,
    string? Spoken = null,
    IReadOnlyList<WheelItem>? Children = null)
{
    /// <summary>Whether picking this slice opens another wheel instead of sending.</summary>
    public bool OpensSubWheel => Children is { Count: > 0 };
}

/// <summary>
/// What the radio wheel offers and what it types, kept apart from the window that draws it so it
/// can be tested without one.
///
/// The wheel is the fourth way into <see cref="CallCatalog"/>, after the radio, the F10 menu and
/// the chat: it puts a line like <c>» Overlord: Picture</c> into a folder, the client hook
/// <c>dcs\Scripts\Hooks\Darkstar-Wheel.lua</c> sends it as chat, and from there it is a chat
/// request like any other (<see cref="ChatBridge"/>).
///
/// Seven slices at the top, so that every one has a direction on an eight-way hat: the four
/// tactical calls, the threat circle and the airfield calls as two sub-wheels, and the radio check.
/// A sub-wheel never has more than seven slices either, for the same reason.
/// </summary>
public static class RadioWheel
{
    /// <summary>The most slices one wheel may have and still give each a hat direction.</summary>
    public const int MaxSlices = 7;

    /// <summary>
    /// The top level, clockwise from the top. Always the same slices in the same places - a wheel
    /// is used by direction, without looking, and a slice that moves depending on the situation is
    /// one the thumb misses.
    /// </summary>
    public static IReadOnlyList<WheelItem> Build(WheelSettings settings)
    {
        WheelItem Call(string id, WheelGroup group, WheelIcon icon)
        {
            var call = CallCatalog.ById(id)
                       ?? throw new InvalidOperationException($"The call catalogue has no \"{id}\".");
            return new WheelItem(call.MenuLabel, group, icon, call.MenuLabel, call.EchoPhrase);
        }

        var radii = (settings.ThreatCircleRadii is { Count: > 0 } configured ? configured : WheelSettings.DefaultRadii.ToList())
            .Take(MaxSlices - 1)
            .Select(r => new WheelItem(
                $"{r} NM", WheelGroup.Tactical, WheelIcon.None,
                $"Threat circle {r}", $"threat circle {r} miles"))
            .Append(new WheelItem("Off", WheelGroup.Tactical, WheelIcon.Off, "Cancel threat circle", "cancel threat circle"))
            .ToList();

        var airfield = new List<WheelItem>
        {
            Call("runway_in_use", WheelGroup.Airfield, WheelIcon.Runway),
            Call("atis", WheelGroup.Airfield, WheelIcon.Windsock),
            Call("divert", WheelGroup.Airfield, WheelIcon.Turn),
        };

        return new List<WheelItem>
        {
            Call("bogey_dope", WheelGroup.Tactical, WheelIcon.Crosshair),
            Call("threat_check", WheelGroup.Tactical, WheelIcon.Shield),
            Call("picture", WheelGroup.Tactical, WheelIcon.Radar),
            Call("alpha_check", WheelGroup.Tactical, WheelIcon.Pin),
            new("Threat circle", WheelGroup.Tactical, WheelIcon.ThreatCircle, Children: radii),
            new("Airfield", WheelGroup.Airfield, WheelIcon.Tower, Children: airfield),
            Call("radio_check", WheelGroup.Radio, WheelIcon.Radio),
        };
    }

    /// <summary>The line typed into the chat: <c>» Overlord: Picture</c>.</summary>
    public static string ChatLine(WheelSettings settings, WheelItem item) =>
        $"{settings.Marker} {settings.Callsign}: {item.ChatText}";

    /// <summary>
    /// Which slice a hat direction points at, for a wheel of <paramref name="count"/> slices with the
    /// first at the top. <paramref name="pov"/> is what Windows reports for a hat: hundredths of a
    /// degree clockwise from forward, or 65535 when it is centred. Null when centred.
    /// </summary>
    public static int? SliceForHat(int pov, int count)
    {
        if (count <= 0 || pov < 0 || pov >= 36000) return null;

        var degrees = pov / 100.0;
        var width = 360.0 / count;
        return (int)Math.Round(degrees / width, MidpointRounding.AwayFromZero) % count;
    }

    /// <summary>
    /// The folder the wheel writes to and the client hook reads: <c>%TEMP%\Darkstar-Wheel\Out</c>.
    /// Both run as the same Windows user on the player's PC, so the temp folder is the one place
    /// both find without being told where DCS keeps its Saved Games.
    /// </summary>
    public static string OutboxFolder => Path.Combine(Path.GetTempPath(), "Darkstar-Wheel", "Out");

    private static int _written;

    /// <summary>
    /// Puts one line into the outbox and returns the file's path. Written as .tmp and renamed, so
    /// the hook never reads half a line; named by time and a counter, so it sends in order.
    /// </summary>
    public static string Write(string folder, string line)
    {
        Directory.CreateDirectory(folder);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var counter = Interlocked.Increment(ref _written) % 100000;
        var name = $"{stamp}-{counter:00000}";

        var temp = Path.Combine(folder, name + ".tmp");
        var path = Path.Combine(folder, name + ".txt");

        var oneLine = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        File.WriteAllText(temp, oneLine + "\n", new UTF8Encoding(false));
        File.Move(temp, path);
        return path;
    }
}
