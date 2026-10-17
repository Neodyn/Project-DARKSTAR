using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// What the log says about where the settings came from.
///
/// This exists because of a real afternoon lost to it: two warnings said a setting was empty,
/// the config.json on screen plainly had it, and nothing anywhere named the file the bot had
/// actually opened. Everything here is therefore about one question - can a reader tell which
/// copy the bot read - so the checks are about the wording and the ordering as much as the
/// values. A banner that is merely present but not above the warnings it explains would not have
/// helped.
/// </summary>
internal static class StartupPathTests
{
    /// <summary>
    /// Stand-in for the bot's own folder. Deliberately not a rooted path: Describe neither opens
    /// nor resolves it, and a drive-letter literal here would trip the suite's own guard against
    /// absolute paths in test sources - which is a guard worth keeping, since it cannot tell a
    /// path a test merely prints from one it is about to open.
    ///
    /// On a real installation the banner's first line reads
    /// "Settings read from: K:\Darkstar\BOT_SRS".
    /// </summary>
    private const string Folder = "darkstar\\BOT_SRS";

    private static SettingsFile Found(string name, long bytes, DateTime at) =>
        new(name, $"{Folder}\\{name}", true, bytes, at);

    /// <summary>
    /// Where the text after the padded file name begins. The size itself varies in width ("96
    /// bytes" against "7.8 kB"), so the column has to be measured from the start of the line
    /// rather than from anything inside the detail.
    /// </summary>
    private static int DetailColumn(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s+\S+\s+");
        return match.Success ? match.Length : -1;
    }

    public static void Run()
    {
        Section("The banner names the folder and every file in it");

        var at = new DateTime(2026, 10, 17, 11, 4, 2, DateTimeKind.Local);
        var files = new List<SettingsFile>
        {
            Found("config.json", 7_983, at),
            Found("phrases.json", 412, at.AddDays(-45)),
            Found("vocabulary.json", 96, at.AddDays(-45)),
        };

        var lines = StartupPaths.Describe(Folder, files);
        var all = string.Join("\n", lines);

        Check("the folder is the first thing said",
            lines[0].Contains(Folder, StringComparison.Ordinal), lines[0]);
        Check("every file gets a line",
            StartupPaths.FileNames.All(n => all.Contains(n, StringComparison.Ordinal)), all);
        Check("with its size", all.Contains("7.8 kB", StringComparison.Ordinal), all);
        Check("and when it was last changed",
            all.Contains("2026-10-17 11:04:02", StringComparison.Ordinal), all);

        // The sentence that does the actual work: it is what turns "my setting is empty" into
        // "I am editing a different file".
        Check("and the reader is told that nothing else is read",
            all.Contains("only one the bot reads", StringComparison.Ordinal), all);
        Check("and that the warnings below refer to it",
            all.Contains("warning below", StringComparison.Ordinal), all);

        // Three paths are only comparable at a glance if they start at the same column - which is
        // the whole reason a stale timestamp among two fresh ones gets noticed.
        var fileLines = lines.Where(l => l.StartsWith("  ", StringComparison.Ordinal)).ToList();
        Check("one line per file", fileLines.Count == 3, $"{fileLines.Count}");
        Check("the detail starts at the same column on each",
            fileLines.Select(DetailColumn).Distinct().Count() == 1,
            string.Join(" | ", fileLines.Select(l => $"{DetailColumn(l)}:{l}")));

        Section("A file that is not there says so, rather than reading as empty");

        // "config.json - GeminiApiKey is empty" on a run that created the file is the single most
        // misleading pair of lines this project can emit, so the missing case gets its own wording.
        var missing = StartupPaths.Describe(Folder, new List<SettingsFile>
        {
            new("config.json", $"{Folder}\\config.json", false, 0, null),
        });
        Check("it is called missing", string.Join(" ", missing).Contains("not there", StringComparison.Ordinal),
            string.Join(" | ", missing));
        Check("and the reader is told one will be created",
            string.Join(" ", missing).Contains("will be created", StringComparison.Ordinal),
            string.Join(" | ", missing));
        Check("no size is invented for it",
            !string.Join(" ", missing).Contains("0 byte", StringComparison.Ordinal),
            string.Join(" | ", missing));

        Section("A file that could not be looked at is not reported as missing");

        // A settings folder on a network drive that has gone away produces neither a file nor an
        // absence. Calling it missing would send somebody to create a config they already have.
        var unreadable = StartupPaths.Describe(Folder, new List<SettingsFile>
        {
            new("config.json", $"{Folder}\\config.json", false, 0, null, "The device is not ready."),
        });
        var unreadableText = string.Join(" ", unreadable);
        Check("the reason is given",
            unreadableText.Contains("The device is not ready.", StringComparison.Ordinal), unreadableText);
        Check("and it is not called missing",
            !unreadableText.Contains("not there", StringComparison.Ordinal), unreadableText);

        Section("Nothing is claimed about a folder with no files listed");

        var nothing = StartupPaths.Describe(Folder, new List<SettingsFile>());
        Check("the folder is still named", nothing.Count == 1, $"{nothing.Count}");
        Check("but nothing is said about which files are read",
            !string.Join(" ", nothing).Contains("only one the bot reads", StringComparison.Ordinal),
            string.Join(" | ", nothing));

        Section("Sizes and timestamps read the same on every machine");

        Check("a small file is counted in bytes", StartupPaths.Size(412) == "412 bytes", StartupPaths.Size(412));
        Check("one byte is singular", StartupPaths.Size(1) == "1 byte", StartupPaths.Size(1));
        Check("an empty file is plural", StartupPaths.Size(0) == "0 bytes", StartupPaths.Size(0));
        Check("the switch to kB is at 1024", StartupPaths.Size(1023) == "1023 bytes", StartupPaths.Size(1023));
        Check("and 1024 is 1 kB", StartupPaths.Size(1024) == "1 kB", StartupPaths.Size(1024));

        // A German Windows would otherwise write "7,8 kB" into a log somebody else is reading -
        // the same reason Program.cs pins the culture.
        Check("the decimal separator is a point, whatever the machine's locale",
            StartupPaths.Size(7_983) == "7.8 kB", StartupPaths.Size(7_983));

        Check("a timestamp is sortable and unambiguous",
            StartupPaths.When(new DateTime(2026, 1, 2, 3, 4, 5)) == "2026-01-02 03:04:05",
            StartupPaths.When(new DateTime(2026, 1, 2, 3, 4, 5)));
        Check("and an absent one is not guessed at",
            StartupPaths.When(null) == "unknown", StartupPaths.When(null));

        Section("Looking at a real folder");

        var folder = Path.Combine(Path.GetTempPath(), "darkstar-startup-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "config.json"), "{\"SrsPort\":5002}");

            var inspected = StartupPaths.Inspect(folder);

            // Spelled out rather than compared against StartupPaths.FileNames: checking the
            // implementation's own list against itself passes however that list is edited, which
            // is no check at all. These three names are the claim.
            Check("every file the bot reads is accounted for",
                inspected.Select(f => f.FileName)
                    .SequenceEqual(new[] { "config.json", "phrases.json", "vocabulary.json" }),
                string.Join(", ", inspected.Select(f => f.FileName)));

            var config = inspected.Single(f => f.FileName == "config.json");
            Check("the one that exists is found", config.Exists);
            Check("its size is read off the disk, not guessed", config.Bytes == 16, $"{config.Bytes}");
            Check("its timestamp is there", config.LastWriteLocal != null);
            Check("nothing is reported as a problem", config.Problem == null, config.Problem ?? "");

            // The path is what somebody pastes into Explorer. A relative one would be useless in
            // a service's log, where the working directory is C:\Windows\System32.
            Check("the path is absolute", Path.IsPathRooted(config.FullPath), config.FullPath);

            // And absolute even when the folder it was handed was not - which an absolute input
            // like the one above cannot show, because Path.Combine would have produced a rooted
            // path either way. A relative folder is the only input that tells the two apart.
            var fromRelative = StartupPaths.Inspect(Path.Combine("some", "relative", "folder"));
            Check("a relative folder is resolved rather than passed through",
                fromRelative.All(f => Path.IsPathRooted(f.FullPath)),
                string.Join(", ", fromRelative.Select(f => f.FullPath)));

            var absent = inspected.Single(f => f.FileName == "phrases.json");
            Check("the ones that do not exist say so", !absent.Exists);
            Check("and are not given a size", absent.Bytes == 0, $"{absent.Bytes}");
            Check("and are still named with a full path",
                Path.IsPathRooted(absent.FullPath), absent.FullPath);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }

        Section("The banner is above the warnings it explains");

        // Ordering is the point. The same lines printed after the config has been loaded and
        // validated would arrive too late to stop the reader trusting the bare name.
        var botSource = ReadSource("src/Darkstar.Bot/BotService.cs");
        var bannerAt = botSource.IndexOf("StartupPaths.Describe", StringComparison.Ordinal);
        var loadAt = botSource.IndexOf("AppConfig.LoadOrCreateDefault", StringComparison.Ordinal);
        Check("the bot prints it at all", bannerAt >= 0);
        Check("and before it reads the configuration", bannerAt >= 0 && loadAt > bannerAt,
            $"banner at {bannerAt}, load at {loadAt}");

        Section("A validation warning names which file it is about");

        // The original bug in one check: a warning saying "config.json" is read as a statement
        // about the file on screen, which is wrong whenever a second copy exists.
        var configSource = ReadSource("src/Darkstar.Core/AppConfig.cs");
        Check("the warnings carry the resolved path",
            configSource.Contains("WARNING: {Path.GetFullPath(configPath)} - {message}", StringComparison.Ordinal));
        Check("and not the bare file name",
            !configSource.Contains("WARNING: config.json - {message}", StringComparison.Ordinal));
    }
}
