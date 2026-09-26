using System.Globalization;
using System.Text.RegularExpressions;

namespace Darkstar;

/// <summary>
/// Finding and reading the bot's log files from outside the bot - which is harder than it looks,
/// because the file being written is the one Windows tells the truth about last.
///
/// THE TRAP: "newest file wins" implemented as "highest LastWriteTime" picks the WRONG file while
/// the bot is running. Windows updates a file's size and modification time in the directory entry
/// lazily - typically only when the last handle closes - so an actively written log can look
/// hours older than a finished one from a previous run. The config editor's log tail used to do
/// exactly that, and the symptom was a log that appeared to fill up only once the bot was stopped.
///
/// The fix is to not ask the file system: the bot names each log after the moment it started
/// (darkstar_yyyy-MM-dd_HH-mm-ss.log), and that name is accurate the instant the file exists.
/// </summary>
public static class LogFiles
{
    /// <summary>Matches the timestamp the bot puts in every log file name.</summary>
    private static readonly Regex NamePattern =
        new(@"darkstar_(?<stamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.log$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>
    /// The most recent log file out of the given paths, or null when there are none.
    ///
    /// Ordered by the timestamp in the file name, because that is the only source that is correct
    /// for a file still being written to. Files whose names don't carry a timestamp (renamed,
    /// copied, kept by hand) are ranked below every properly named one and compared by write time
    /// among themselves - so they are still reachable, but never beat the live log.
    /// </summary>
    public static string? PickNewest(IEnumerable<string>? paths)
    {
        if (paths == null) return null;

        string? best = null;
        DateTime bestStamp = DateTime.MinValue;
        var bestIsNamed = false;

        foreach (var path in paths)
        {
            var named = TryParseTimestamp(path, out var stamp);

            if (!named)
            {
                try { stamp = File.GetLastWriteTimeUtc(path); }
                catch { continue; }
            }

            // A properly named file always beats an unnamed one, whatever the timestamps say.
            if (best == null ||
                (named && !bestIsNamed) ||
                (named == bestIsNamed && stamp > bestStamp))
            {
                best = path;
                bestStamp = stamp;
                bestIsNamed = named;
            }
        }

        return best;
    }

    /// <summary>The start time encoded in a log file's name.</summary>
    public static bool TryParseTimestamp(string? path, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var match = NamePattern.Match(path);
        if (!match.Success) return false;

        return DateTime.TryParseExact(match.Groups["stamp"].Value, StampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    /// <summary>
    /// Reads a log file that another process is currently writing to.
    ///
    /// <c>File.ReadAllLines</c> would do for the bot's own files, but only because the bot happens
    /// to share them generously. Asking for FileShare.ReadWrite explicitly means this also works
    /// on a log written by something stricter, and it cannot start failing if the bot's own
    /// sharing mode is ever tightened.
    /// </summary>
    public static List<string> ReadAllLinesShared(string path)
    {
        var lines = new List<string>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
            lines.Add(line);

        return lines;
    }
}
