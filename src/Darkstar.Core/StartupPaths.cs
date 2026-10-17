using System.Globalization;

namespace Darkstar;

/// <summary>One of the bot's own settings files, as it was found on disk at startup.</summary>
/// <param name="FullPath">Absolute, so it can be pasted straight into Explorer.</param>
/// <param name="LastWriteLocal">
/// Local time, not UTC - it is going to be compared against "but I saved it two minutes ago",
/// and the rest of the log is in local time too.
/// </param>
/// <param name="Problem">
/// Why the file could not be looked at, where that happened. A path on a network drive that has
/// gone away is not the same thing as a missing file, and saying so saves the next hour.
/// </param>
public sealed record SettingsFile(
    string FileName,
    string FullPath,
    bool Exists,
    long Bytes,
    DateTime? LastWriteLocal,
    string? Problem = null);

/// <summary>
/// Where the bot read its settings from, said out loud before it reads them.
///
/// The bot resolves config.json against its own executable's folder, which is correct - a Windows
/// Service starts with C:\Windows\System32 as its working directory, so anything relative would
/// land there - but it used to be invisible: nothing in the log named the file. A warning reading
/// "config.json - GeminiApiKey is empty" therefore looked like a statement about the file the
/// operator had just edited, and it is not whenever a second copy exists: a dev build's
/// bin\Debug\net8.0 folder beside the repository, an old installation beside a new one, a config
/// the editor found by searching upward. Every symptom then points at the settings, when the
/// question is which settings.
///
/// So the folder, the three files, their sizes and when they were last changed go into the log
/// first. A file last changed three weeks ago under a path nobody recognises answers the whole
/// question in one line, and a missing one says it is about to be created rather than used.
///
/// The log file itself is not listed here - Logger.Init already prints it, and naming it twice
/// would mean two places that could disagree.
/// </summary>
public static class StartupPaths
{
    /// <summary>The files the bot reads out of its own folder, in the order it reads them.</summary>
    public static readonly string[] FileNames = { "config.json", "phrases.json", "vocabulary.json" };

    /// <summary>
    /// Looks at the folder. The only part of this class that touches the disk, so everything the
    /// log ends up saying can be checked against a hand-built list instead of against a machine.
    /// </summary>
    public static List<SettingsFile> Inspect(string folder)
    {
        var found = new List<SettingsFile>();

        foreach (var name in FileNames)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(folder, name));
            }
            catch (Exception ex)
            {
                // A folder string that is not a usable path at all. Still worth a line: an
                // unusable path is exactly as interesting as an ignored file.
                found.Add(new SettingsFile(name, Path.Combine(folder, name), false, 0, null, ex.Message));
                continue;
            }

            try
            {
                var info = new FileInfo(full);
                found.Add(info.Exists
                    ? new SettingsFile(name, full, true, info.Length, info.LastWriteTime)
                    : new SettingsFile(name, full, false, 0, null));
            }
            catch (Exception ex)
            {
                found.Add(new SettingsFile(name, full, false, 0, null, ex.Message));
            }
        }

        return found;
    }

    /// <summary>
    /// The banner as lines for the log.
    ///
    /// Pure formatting: no disk access and no path resolution, because resolving a path here
    /// would mean the suite could only check the paths this machine happens to have. The caller
    /// passes absolute values - Inspect already returns them, and the bot's own folder is
    /// absolute to begin with.
    /// </summary>
    public static List<string> Describe(string folder, IReadOnlyList<SettingsFile> files)
    {
        var lines = new List<string> { $"Settings read from: {folder}" };

        if (files.Count == 0) return lines;

        var width = files.Max(f => f.FileName.Length);

        foreach (var file in files)
        {
            var name = file.FileName.PadRight(width);

            lines.Add(file switch
            {
                { Problem: not null } => $"  {name}  could not be looked at: {file.Problem}",
                { Exists: false } => $"  {name}  not there yet - a file with default values will be created",
                _ => $"  {name}  {Size(file.Bytes)}, last changed {When(file.LastWriteLocal)}",
            });
        }

        lines.Add("That folder is the only one the bot reads. A config.json anywhere else has no " +
                  "effect on it, and every warning below about a setting is about the file named here.");

        return lines;
    }

    /// <summary>
    /// Bytes for a small file, kilobytes above that. The number is there to be recognised rather
    /// than measured - a config somebody has filled in does not weigh the same as a fresh default
    /// one - so one decimal is enough and exact byte counts would just be harder to read.
    ///
    /// Public because the editor's profile list shows the same two facts about a file, and a
    /// second way of writing a file size is a second thing to keep in step for no gain.
    /// </summary>
    public static string Size(long bytes) =>
        bytes < 1024
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} byte{(bytes == 1 ? "" : "s")}"
            : $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} kB";

    /// <summary>
    /// Invariant on purpose, like every other number this application writes: the log is read by
    /// whoever is helping, not only by the machine that wrote it.
    /// </summary>
    public static string When(DateTime? at) =>
        at?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "unknown";
}
