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

    /// <summary>
    /// Whether verbose Debug() messages are recorded at all.
    ///
    /// They used to always go to the file regardless, on the theory that a bug report should
    /// carry full detail. Measured, that theory cost about 123 MB of string garbage per hour per
    /// radio and wrote the same again to disk - the UDP receive path alone logs two or three
    /// lines for every 20 ms packet. The detail is still there when it is wanted: turning this on
    /// records everything, and an error dumps the last <see cref="RecentDebugCapacity"/> debug
    /// lines to the file even when it is off, so the run-up to a failure is never lost.
    /// </summary>
    public static bool DebugEnabled { get; set; } = false;

    /// <summary>
    /// Cheap check for call sites that would otherwise build an expensive string for nothing.
    /// The hot paths (per-packet UDP logging) test this before interpolating.
    /// </summary>
    public static bool IsDebugEnabled => Enabled && DebugEnabled;

    /// <summary>How many recent debug lines are kept in memory to give an error its context.</summary>
    private const int RecentDebugCapacity = 200;

    /// <summary>
    /// The last few debug lines, kept in memory rather than written out. Costs one small array
    /// and no I/O, and turns "the error says X and nothing else" into "here is the minute before
    /// it". Dumped to the file by <see cref="Log"/> when the message looks like a failure.
    /// </summary>
    private static readonly string?[] RecentDebug = new string?[RecentDebugCapacity];
    private static int _recentDebugNext;
    private static bool _recentDebugWrapped;

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
    public static void Log(string message)
    {
        // An error is the one moment the verbose detail is worth having, so hand over whatever
        // was kept in memory before writing the error itself.
        if (LooksLikeFailure(message))
            DumpRecentDebug();

        Write(message, forceConsole: true);
    }

    /// <summary>
    /// Verbose/raw message (e.g. every UDP packet, raw TCP lines, volume readings). Recorded in
    /// full when DebugEnabled is on; otherwise kept in a small in-memory ring so an error can
    /// still show what led up to it.
    ///
    /// Callers on a hot path should test <see cref="IsDebugEnabled"/> first: the argument is
    /// evaluated whether or not anything is done with it, and interpolating a string per UDP
    /// packet is most of what this method used to cost.
    /// </summary>
    public static void Debug(string message)
    {
        if (!Enabled) return;

        if (DebugEnabled)
        {
            Write(message, forceConsole: false);
            return;
        }

        lock (Lock)
        {
            RecentDebug[_recentDebugNext] = message;
            _recentDebugNext = (_recentDebugNext + 1) % RecentDebugCapacity;
            if (_recentDebugNext == 0) _recentDebugWrapped = true;
        }
    }

    /// <summary>Markers that make a message worth spending the kept-back detail on.</summary>
    private static bool LooksLikeFailure(string message) =>
        message.Contains("ERROR", StringComparison.Ordinal) ||
        message.Contains("FATAL", StringComparison.Ordinal) ||
        message.Contains("[Error]", StringComparison.Ordinal) ||
        message.Contains(":ERR]", StringComparison.Ordinal);

    /// <summary>
    /// Writes the kept-back debug lines to the file and empties the ring, so the same context is
    /// not repeated for every follow-up error.
    /// </summary>
    private static void DumpRecentDebug()
    {
        lock (Lock)
        {
            var count = _recentDebugWrapped ? RecentDebugCapacity : _recentDebugNext;
            if (count == 0) return;

            try
            {
                _fileWriter?.WriteLine($"--- last {count} verbose line(s) before the message below ---");

                var start = _recentDebugWrapped ? _recentDebugNext : 0;
                for (var i = 0; i < count; i++)
                {
                    var entry = RecentDebug[(start + i) % RecentDebugCapacity];
                    if (entry != null) _fileWriter?.WriteLine($"    {entry}");
                }

                _fileWriter?.WriteLine("--- end of verbose context ---");
            }
            catch { /* best effort */ }

            Array.Clear(RecentDebug);
            _recentDebugNext = 0;
            _recentDebugWrapped = false;
        }
    }

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
