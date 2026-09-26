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

    /// <summary>
    /// The underlying stream, kept separately because only FileStream can force the data all the
    /// way to disk - see FlushToDiskIfDue for why that is necessary and not just tidy.
    /// </summary>
    private static FileStream? _fileStream;

    /// <summary>Full path of the current log file, for telling the user where to look.</summary>
    public static string? CurrentLogFile { get; private set; }

    private static DateTime _lastDiskFlush = DateTime.MinValue;

    /// <summary>
    /// How often the log is forced all the way to disk. A forced flush per line would mean a disk
    /// round trip for every UDP packet at debug level; a second of lag is invisible to someone
    /// watching a log.
    /// </summary>
    private static readonly TimeSpan DiskFlushInterval = TimeSpan.FromSeconds(1);

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

            // Opened by hand rather than through the StreamWriter constructor, for two reasons.
            //
            // FileShare.ReadWrite | Delete: the default only grants readers FileShare.Read, which
            // is enough for Notepad but not for the tail tools people actually watch logs with
            // (Get-Content -Wait, baretail, ...) - those ask for write sharing and are refused.
            //
            // And keeping the FileStream lets us force the data to disk. StreamWriter.AutoFlush
            // pushes it into the OS cache, which is enough for another process to READ - but it
            // does not update the file's size and timestamp in the directory entry. Windows does
            // that lazily, usually only when the handle closes, which is why an actively written
            // log looks 0 bytes long in Explorer and older than it is to anything sorting by
            // modification time.
            _fileStream = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            _fileWriter = new StreamWriter(_fileStream) { AutoFlush = true };
            CurrentLogFile = Path.GetFullPath(path);

            Log($"Logging started, file: {CurrentLogFile}");
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

            try
            {
                _fileWriter?.WriteLine(line);
                FlushToDiskIfDue();
            }
            catch { /* file logging is best-effort, must not stop the bot */ }
        }
    }

    /// <summary>
    /// Forces the log all the way to disk, at most once per <see cref="DiskFlushInterval"/>.
    ///
    /// This is what makes the file look right from outside while the bot is running: the content
    /// is already readable after AutoFlush, but the size and modification time in the directory
    /// entry are not, and plenty of things - Explorer, and anything picking "the newest log" -
    /// go by exactly those. Must be called while holding the lock.
    /// </summary>
    private static void FlushToDiskIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastDiskFlush < DiskFlushInterval) return;

        _lastDiskFlush = now;
        try { _fileStream?.Flush(flushToDisk: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Forces everything written so far to disk, regardless of the interval. For the moments where
    /// the next thing that happens might be the process disappearing.
    /// </summary>
    public static void Flush()
    {
        lock (Lock)
        {
            try
            {
                _fileWriter?.Flush();
                _fileStream?.Flush(flushToDisk: true);
                _lastDiskFlush = DateTime.UtcNow;
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Closes the log file properly. Without this the file was only ever closed by the runtime on
    /// process exit - which worked, but meant a crash could lose the last lines, exactly the ones
    /// explaining the crash.
    /// </summary>
    public static void Shutdown()
    {
        lock (Lock)
        {
            try
            {
                _fileWriter?.Flush();
                _fileStream?.Flush(flushToDisk: true);
                _fileWriter?.Dispose(); // disposes the stream it wraps
            }
            catch { /* best effort */ }
            finally
            {
                _fileWriter = null;
                _fileStream = null;
            }
        }
    }
}
