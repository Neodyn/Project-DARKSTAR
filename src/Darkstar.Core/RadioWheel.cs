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
    }
}

/// <summary>
/// What the radio wheel offers and what it types, kept apart from the window that draws it so it
/// can be tested without one.
///
/// The wheel is the fourth way into <see cref="CallCatalog"/>, after the radio, the F10 menu and
/// the chat: it puts a line like <c>» Overlord: Picture</c> into a folder, the client hook
/// <c>dcs\Scripts\Hooks\Darkstar-Wheel.lua</c> sends it as chat, and from there it is a chat
/// request like any other (<see cref="ChatBridge"/>).
/// </summary>
public static class RadioWheel
{
    /// <summary>
    /// The slices, clockwise from the top. Exactly the calls the F10 menu offers, in the
    /// catalogue's order - and always all of them, in the same place: a wheel is used by direction,
    /// without looking, and a slice that moves depending on the situation is one the thumb misses.
    /// </summary>
    public static IReadOnlyList<CallDefinition> Slices { get; } =
        CallCatalog.All.Where(CallCatalog.MenuOffers).ToList();

    /// <summary>The line typed into the chat: <c>» Overlord: Picture</c>.</summary>
    public static string ChatLine(WheelSettings settings, CallDefinition call) =>
        $"{settings.Marker} {settings.Callsign}: {call.MenuLabel}";

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
