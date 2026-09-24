namespace Darkstar;

/// <summary>
/// Central logging: writes color-coded output to the console (color depends on the detected
/// category in the message text) and simultaneously writes plain, timestamped lines to a log
/// file under logs/. The logs/ directory is created automatically on first use if missing.
///
/// Two levels:
/// - Log(): "essential" messages (connection status, hotword, STT, reply, errors, ...) -
///   always to console AND file.
/// - Debug(): very verbose/raw messages (every single UDP packet, raw TCP JSON lines,
///   volume readings, hex dumps, ...) - ALWAYS to the file, on the console only when
///   DebugEnabled = true (see config.json -> DebugLogging).
/// </summary>
public static class Logger
{
    private static readonly object Lock = new();
    private static StreamWriter? _fileWriter;

    /// <summary>Whether Debug() messages additionally appear on the console (they always go to the file).</summary>
    public static bool DebugEnabled { get; set; } = false;

    /// <summary>
    /// Global kill switch: when false, Log()/Debug() do nothing at all - no console output,
    /// no file write. Saves I/O and some CPU time (color switching, timestamp formatting),
    /// e.g. for performance-critical continuous-operation scenarios.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    // When running as a Windows Service there is no attached console at all - touching
    // Console.ForegroundColor/WriteLine in that case throws. Detected once at startup so we
    // silently skip console output instead of crashing on every single log call.
    private static readonly bool ConsoleAvailable = DetectConsoleAvailable();

    private static bool DetectConsoleAvailable()
    {
        try
        {
            _ = Console.WindowHeight; // throws if there is no console (e.g. running as a Windows Service)
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Order matters: from specific to general, the first match wins.
    private static readonly (string Marker, ConsoleColor Color)[] CategoryColors =
    {
        ("[SRS UDP HEX]", ConsoleColor.DarkGray),
        ("[Hotword Debug]", ConsoleColor.DarkGray),
        ("[SRS -> Server]", ConsoleColor.Cyan),
        ("[SRS <- Server]", ConsoleColor.DarkCyan),
        ("[SRS UDP]", ConsoleColor.Blue),
        ("[SRS]", ConsoleColor.Blue),
        ("[Watchdog]", ConsoleColor.Yellow),
        ("WARNING", ConsoleColor.Yellow),
        ("[Error]", ConsoleColor.Red),
        (":ERR]", ConsoleColor.Red),
        ("[Gemini] Error", ConsoleColor.Red),
        ("[Hotword Detected]", ConsoleColor.Green),
        ("Wake word detection active", ConsoleColor.Green),
        ("[Reply]", ConsoleColor.Green),
        ("[Phrase Match]", ConsoleColor.Magenta),
        ("[STT]", ConsoleColor.Magenta),
        ("[Recording finished]", ConsoleColor.Cyan),
        ("config.json", ConsoleColor.Yellow),
        ("Connected.", ConsoleColor.Green),
        ("Connecting to", ConsoleColor.Cyan),
    };

    /// <summary>
    /// Must be called once at startup. Creates logs/ (if needed) and opens a new log file for
    /// this run, named after the start time.
    /// </summary>
    public static void Init(string logsDirectory = "logs")
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            var fileName = $"darkstar_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log";
            var path = Path.Combine(logsDirectory, fileName);
            _fileWriter = new StreamWriter(path, append: true) { AutoFlush = true };
            Log($"Logging started, file: {Path.GetFullPath(path)}");
        }
        catch (Exception ex)
        {
            // Logging must never crash the bot - keep running without file logging.
            if (ConsoleAvailable)
            {
                try { Console.WriteLine($"[Logger] Could not create the log file, logging to console only: {ex.Message}"); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>Essential message - always to console and file.</summary>
    public static void Log(string message) => Write(message, forceConsole: true);

    /// <summary>
    /// Verbose/raw message (e.g. every UDP packet, raw TCP lines, volume readings) - always
    /// goes to the log file, on the console only when DebugEnabled is on.
    /// </summary>
    public static void Debug(string message) => Write(message, forceConsole: false);

    private static void Write(string message, bool forceConsole)
    {
        if (!Enabled) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] {message}";

        lock (Lock)
        {
            if (ConsoleAvailable && (forceConsole || DebugEnabled))
            {
                var color = ConsoleColor.Gray;
                foreach (var (marker, markerColor) in CategoryColors)
                {
                    if (message.Contains(marker, StringComparison.Ordinal))
                    {
                        color = markerColor;
                        break;
                    }
                }

                try
                {
                    var previous = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                    Console.WriteLine(line);
                    Console.ForegroundColor = previous;
                }
                catch
                {
                    /* best effort - never let console output crash the bot */
                }
            }

            try { _fileWriter?.WriteLine(line); }
            catch { /* file logging is best-effort, must not stop the bot */ }
        }
    }
}
