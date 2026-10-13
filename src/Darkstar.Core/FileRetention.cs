namespace Darkstar;

/// <summary>
/// Deciding which of the bot's own files to delete, so that <c>recordings\</c> and <c>logs\</c>
/// stop growing until somebody notices.
///
/// Recordings run at roughly 100 KB per second of speech and a log file is written per start, so
/// both grow without limit on a server that is simply left running. Documenting that as a warning
/// was the earlier answer; this is the honest one.
///
/// The decision is a pure function of the file list, so it can be tested with numbers instead of
/// by filling a disk. Nothing here deletes anything - see <see cref="Prune"/> for that.
/// </summary>
public static class FileRetention
{
    /// <summary>One file, as far as the decision is concerned.</summary>
    /// <param name="Path">Full path, returned unchanged in the result.</param>
    /// <param name="SizeBytes">Its size.</param>
    /// <param name="Timestamp">
    /// When it was created. Taken from the name where the bot puts one there, because a file
    /// still being written has an unreliable modification time (see LogFiles).
    /// </param>
    public sealed record Candidate(string Path, long SizeBytes, DateTime Timestamp);

    /// <summary>What to throw away, and why - the reason is logged rather than guessed at later.</summary>
    /// <param name="TooOld">Files past the age limit.</param>
    /// <param name="OverBudget">Files deleted because the folder was over its size budget, oldest first.</param>
    public sealed record Decision(List<Candidate> TooOld, List<Candidate> OverBudget)
    {
        public IEnumerable<Candidate> All => TooOld.Concat(OverBudget);
        public int Count => TooOld.Count + OverBudget.Count;
        public long BytesFreed => All.Sum(c => c.SizeBytes);
    }

    /// <summary>
    /// Works out what to delete. Age first, then size: an old file goes because it is old, and
    /// what remains is trimmed oldest-first until it fits the budget.
    /// </summary>
    /// <param name="candidates">Every file in the folder.</param>
    /// <param name="maxAgeDays">Delete files older than this. Zero or less disables the age rule.</param>
    /// <param name="maxTotalMegabytes">Keep the folder under this. Zero or less disables the size rule.</param>
    /// <param name="alwaysKeepNewest">
    /// Never delete this many of the newest files, whatever the rules say. The bot's current log
    /// is in the folder it is pruning, and a size budget somebody set to 1 MB must not be able to
    /// delete the file being written to.
    /// </param>
    /// <param name="now">The current time, injected so tests don't depend on the clock.</param>
    public static Decision Decide(
        IEnumerable<Candidate> candidates,
        double maxAgeDays,
        double maxTotalMegabytes,
        int alwaysKeepNewest,
        DateTime now)
    {
        // Newest first: everything below works on "the end of this list is the most expendable".
        var ordered = candidates
            .OrderByDescending(c => c.Timestamp)
            .ThenByDescending(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var protectedCount = Math.Max(0, alwaysKeepNewest);
        var expendable = ordered.Skip(protectedCount).ToList();

        var tooOld = new List<Candidate>();
        if (maxAgeDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(maxAgeDays);
            tooOld.AddRange(expendable.Where(c => c.Timestamp < cutoff));
        }

        var overBudget = new List<Candidate>();
        if (maxTotalMegabytes > 0)
        {
            var budget = (long)(maxTotalMegabytes * 1024 * 1024);

            // The protected files count against the budget - they are really on the disk - but
            // cannot be deleted to meet it.
            var total = ordered.Sum(c => c.SizeBytes) - tooOld.Sum(c => c.SizeBytes);

            // Oldest first, skipping anything already going for being too old.
            foreach (var candidate in expendable.AsEnumerable().Reverse())
            {
                if (total <= budget) break;
                if (tooOld.Contains(candidate)) continue;

                overBudget.Add(candidate);
                total -= candidate.SizeBytes;
            }
        }

        return new Decision(tooOld, overBudget);
    }

    /// <summary>
    /// Applies <see cref="Decide"/> to a folder. Best-effort throughout: a file that is locked or
    /// vanished is skipped, because tidying up must never interfere with the radio work.
    /// </summary>
    /// <param name="directory">Folder to prune. Missing is not an error.</param>
    /// <param name="searchPattern">e.g. "*.wav".</param>
    /// <param name="label">What to call these files in the log ("recording", "log file").</param>
    /// <returns>How many files were deleted and how many bytes that freed.</returns>
    public static (int Deleted, long BytesFreed) Prune(
        string directory,
        string searchPattern,
        double maxAgeDays,
        double maxTotalMegabytes,
        int alwaysKeepNewest,
        string label)
    {
        if (maxAgeDays <= 0 && maxTotalMegabytes <= 0) return (0, 0);

        List<Candidate> candidates;
        try
        {
            if (!Directory.Exists(directory)) return (0, 0);

            candidates = new List<Candidate>();
            foreach (var path in Directory.GetFiles(directory, searchPattern))
            {
                try
                {
                    var info = new FileInfo(path);

                    // Prefer the timestamp the bot wrote into the name; fall back to the file
                    // system for anything renamed or copied in by hand.
                    var timestamp = LogFiles.TryParseTimestamp(path, out var fromName)
                        ? fromName
                        : TimestampFromRecordingName(path) ?? info.LastWriteTime;

                    candidates.Add(new Candidate(path, info.Length, timestamp));
                }
                catch
                {
                    // Unreadable entry - leave it alone rather than guessing about it.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Retention] Could not list {directory}: {ex.Message}");
            return (0, 0);
        }

        var decision = Decide(candidates, maxAgeDays, maxTotalMegabytes, alwaysKeepNewest, DateTime.Now);
        if (decision.Count == 0) return (0, 0);

        var deleted = 0;
        long freed = 0;

        foreach (var candidate in decision.All)
        {
            try
            {
                File.Delete(candidate.Path);
                deleted++;
                freed += candidate.SizeBytes;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Retention] Could not delete {candidate.Path}: {ex.Message}");
            }
        }

        if (deleted > 0)
        {
            Logger.Log($"[Retention] Removed {deleted} {label}(s), freeing {freed / 1024.0 / 1024.0:0.#} MB " +
                       $"({decision.TooOld.Count} past the age limit, {decision.OverBudget.Count} over the size budget).");
        }

        return (deleted, freed);
    }

    /// <summary>
    /// The timestamp in a recording's name, which BotService writes as
    /// "2026-09-27_14-32-05.123_251.000MHz_hit_Punch 1-1.wav".
    /// </summary>
    internal static DateTime? TimestampFromRecordingName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length < 23) return null;

        // Fixed-width prefix, so a plain parse is enough - and cheaper than a regex for a folder
        // that can hold thousands of files.
        var prefix = name[..23];
        return DateTime.TryParseExact(prefix, "yyyy-MM-dd_HH-mm-ss.fff",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
