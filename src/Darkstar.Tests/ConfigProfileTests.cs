using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// Several named config.json files kept side by side, and the editor remembering which folder the
/// bot's own one is in.
///
/// Two things here can do real damage rather than merely be wrong. A profile name becomes a file
/// name, so a name that escapes the Profiles folder writes wherever it likes; and activating a
/// profile overwrites the config.json a working bot reads, so a profile that cannot be parsed must
/// be refused before anything is copied. Both get their own section below, and both are checked by
/// what ends up on disk rather than by what the method returned.
/// </summary>
internal static class ConfigProfileTests
{
    /// <summary>A folder of its own per test, deleted afterwards whatever happens.</summary>
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "darkstar-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Scrap(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A check whose claim is "answers without throwing". An exception counts as a failure of that
    /// check rather than of the whole run.
    ///
    /// Worth the helper: several methods here promise to swallow a broken file and answer anyway,
    /// and a test that calls them bare turns a regression into an aborted suite with no output -
    /// exit -6 and nothing named, which is the one failure mode that tells you nothing. Proven by
    /// making each of those methods rethrow and watching a [FAIL] appear.
    /// </summary>
    private static bool Quietly(Func<bool> check)
    {
        try { return check(); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether the call refused, whatever it threw.
    ///
    /// Deliberately not typed to one exception: what matters about activating a profile that is
    /// not there is that config.json survives, and insisting on FileNotFoundException over
    /// JsonException makes the test fail for a reason nobody cares about - and abort the run while
    /// doing it.
    /// </summary>
    private static bool Refuses(Action call)
    {
        try { call(); return false; }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// What the call complained about.
    ///
    /// Refuses above checks that something was refused; this checks that the complaint names the
    /// right problem. The two are not the same, and the difference is not cosmetic: a missing
    /// profile reported as "the profile is empty" sends somebody looking inside a file that is not
    /// there. Found by sabotage - removing the existence check left the operation just as safe and
    /// its message just as wrong, and nothing noticed.
    /// </summary>
    private static string Complaint(Action call)
    {
        try { call(); return "(it did not refuse at all)"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Files matching a pattern in a folder that may not exist at all.</summary>
    private static string[] FilesIn(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();

    public static void Run()
    {
        Section("A name that could escape the folder is refused");

        // The whole point. A profile name is pasted into a path, so these are not cosmetic rules.
        Check("\"..\" on its own", ConfigProfiles.NameProblem("..") != null);
        Check("a relative step out",
            ConfigProfiles.NameProblem("../config") != null, ConfigProfiles.NameProblem("../config") ?? "accepted!");
        Check("a Windows one",
            ConfigProfiles.NameProblem("..\\config") != null, ConfigProfiles.NameProblem("..\\config") ?? "accepted!");
        Check("one buried in the middle",
            ConfigProfiles.NameProblem("a/../../b") != null, ConfigProfiles.NameProblem("a/../../b") ?? "accepted!");
        Check("a single dot", ConfigProfiles.NameProblem(".") != null);
        Check("and the message says what the rule is",
            ConfigProfiles.NameProblem("../x")!.Contains("..", StringComparison.Ordinal),
            ConfigProfiles.NameProblem("../x")!);

        // And it is refused by throwing rather than by being cleaned up: a sanitized "..\config"
        // still writes somewhere, just somewhere harder to predict.
        Check("FileFor refuses it instead of repairing it",
            Refuses(() => ConfigProfiles.FileFor(NewFolderOnce(), "..\\config")));

        Section("Characters and names the file system cannot carry");

        foreach (var bad in new[] { "a\\b", "a/b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b" })
            Check($"\"{bad}\"", ConfigProfiles.NameProblem(bad) != null, ConfigProfiles.NameProblem(bad) ?? "accepted!");

        // Checked against a fixed list rather than Path.GetInvalidFileNameChars(): on Linux that
        // method returns the separator and NUL and nothing else, so a colon would pass a check
        // written that way and then be refused by the only OS this bot runs on. This suite runs on
        // both, which is how that would have been missed.
        Check("a colon is refused even where the running OS would allow it",
            ConfigProfiles.NameProblem("Server: Training") != null);

        Check("the message names the offending character",
            ConfigProfiles.NameProblem("a|b")!.Contains('|', StringComparison.Ordinal),
            ConfigProfiles.NameProblem("a|b")!);

        Check("a control character", ConfigProfiles.NameProblem("a\tb") != null);
        Check("a trailing dot, which Windows drops silently",
            ConfigProfiles.NameProblem("Training.") != null);

        // A trailing space is the same trap on Windows, but it cannot get this far: Trim() removes
        // every Unicode space, the non-breaking one included, so the name is "Training" by the time
        // it is judged. Checked here so the absence of that rule is on purpose rather than missing.
        Check("a trailing space is trimmed away rather than refused",
            ConfigProfiles.NameProblem("Training  ") == null
            && ConfigProfiles.Normalize("Training  ") == "Training",
            ConfigProfiles.Normalize("Training  "));

        foreach (var reserved in new[] { "CON", "con", "Nul", "COM1", "LPT9", "AUX", "PRN" })
            Check($"the device name \"{reserved}\"",
                ConfigProfiles.NameProblem(reserved) != null, ConfigProfiles.NameProblem(reserved) ?? "accepted!");

        // CON.json is the console device too, so the extension does not rescue it.
        Check("a device name with an extension on it", ConfigProfiles.NameProblem("CON.json") != null);

        // But only the exact names - refusing anything starting with "con" would reject
        // "Configuration test", which is a name somebody would reasonably pick.
        foreach (var fine in new[] { "CONS", "Control tower", "COM0", "COM10", "LPT0", "Configuration test" })
            Check($"\"{fine}\" is not a device name", ConfigProfiles.NameProblem(fine) == null,
                ConfigProfiles.NameProblem(fine) ?? "");

        Section("Names people would actually type are accepted");

        foreach (var good in new[]
                 {
                     "Training", "Campaign - towers and tanker", "Server #2", "v1.2 settings",
                     "Overlord (test)", "Kampagne über Syrien", "AWACS only",
                 })
            Check($"\"{good}\"", ConfigProfiles.NameProblem(good) == null, ConfigProfiles.NameProblem(good) ?? "");

        Check("an empty name is not", ConfigProfiles.NameProblem("") != null);
        Check("nor whitespace only", ConfigProfiles.NameProblem("   ") != null);
        Check("nor null", ConfigProfiles.NameProblem(null) != null);

        Check($"a name over {ConfigProfiles.MaxNameLength} characters is refused",
            ConfigProfiles.NameProblem(new string('x', ConfigProfiles.MaxNameLength + 1)) != null);
        Check("and exactly that many is not",
            ConfigProfiles.NameProblem(new string('x', ConfigProfiles.MaxNameLength)) == null);

        Check("surrounding whitespace is trimmed, not rejected",
            ConfigProfiles.NameProblem("  Training  ") == null);
        Check("and trimmed away for real", ConfigProfiles.Normalize("  Training  ") == "Training",
            ConfigProfiles.Normalize("  Training  "));
        Check("null normalizes to empty", ConfigProfiles.Normalize(null) == "");

        Section("Where a profile lands");

        var folder = NewFolder();
        try
        {
            Check("the Profiles folder is under the config folder",
                ConfigProfiles.FolderFor(folder) == Path.Combine(folder, "Profiles"),
                ConfigProfiles.FolderFor(folder));

            var path = ConfigProfiles.FileFor(folder, "Training");
            Check("a profile is a .json in it",
                path == Path.Combine(folder, "Profiles", "Training.json"), path);
            Check("and the name comes back off the file", ConfigProfiles.NameOf(path) == "Training",
                ConfigProfiles.NameOf(path));

            Check("a folder with no Profiles subfolder has no profiles, rather than an error",
                Quietly(() => ConfigProfiles.List(folder).Count == 0));

            Section("Saving, listing and deleting");

            var config = new AppConfig { BotCallsign = "Overlord", FrequencyHz = 251_000_000 };
            ConfigProfiles.Save(folder, "Training", config);

            Check("the Profiles folder is created on the way",
                Directory.Exists(ConfigProfiles.FolderFor(folder)));
            Check("the profile is on disk", File.Exists(path));
            Check("and is listed", ConfigProfiles.List(folder).Single().Name == "Training");
            Check("with a size and a date",
                ConfigProfiles.List(folder).Single() is { Bytes: > 0 } p && p.LastWriteLocal.Year > 2000);
            Check("Exists agrees", ConfigProfiles.Exists(folder, "Training"));
            Check("and says no to one that is not there", !ConfigProfiles.Exists(folder, "Campaign"));

            // What was saved has to be the settings that were passed in, not a default config.
            Check("the settings in it are the ones handed over",
                AppConfig.LoadReadOnly(path)!.BotCallsign == "Overlord",
                AppConfig.LoadReadOnly(path)!.BotCallsign);

            ConfigProfiles.Save(folder, "Campaign", new AppConfig { BotCallsign = "Magic" });
            ConfigProfiles.Save(folder, "approach", new AppConfig());

            var listed = ConfigProfiles.List(folder).Select(x => x.Name).ToList();
            Check("all three are listed", listed.Count == 3, string.Join(", ", listed));
            Check("sorted by name, ignoring case",
                listed.SequenceEqual(new[] { "approach", "Campaign", "Training" }), string.Join(", ", listed));

            ConfigProfiles.Delete(folder, "approach");
            Check("a deleted profile is gone",
                !ConfigProfiles.List(folder).Any(x => x.Name == "approach"));
            Check("and the others are untouched", ConfigProfiles.List(folder).Count == 2);

            // Deleting the wrong profile is a mistake, not an edit - the backup is the only undo.
            var backups = FilesIn(Path.Combine(ConfigProfiles.FolderFor(folder), "Backup"), "approach*");
            Check("a copy of it was kept", backups.Length > 0,
                $"{backups.Length} backup(s)");

            Check("deleting one that is not there says so",
                Refuses(() => ConfigProfiles.Delete(folder, "never existed")));

            Section("Activating one overwrites config.json, and only ever that file");

            var livePath = Path.Combine(folder, "config.json");
            new AppConfig { BotCallsign = "Before" }.Save(livePath);

            ConfigProfiles.Activate(folder, "Campaign");

            Check("config.json now holds the profile",
                AppConfig.LoadReadOnly(livePath)!.BotCallsign == "Magic",
                AppConfig.LoadReadOnly(livePath)!.BotCallsign);
            Check("the profile itself is left in place", File.Exists(ConfigProfiles.FileFor(folder, "Campaign")));

            // The config that was replaced has to be recoverable: somebody activating a profile to
            // try it out expects to get back what they had.
            var configBackups = FilesIn(Path.Combine(folder, "Backup"), "config*");
            Check("the config that was there was backed up first", configBackups.Length > 0);
            Check("and the backup is the old one",
                configBackups.Any(b => AppConfig.LoadReadOnly(b)?.BotCallsign == "Before"),
                string.Join(", ", configBackups.Select(Path.GetFileName)));

            Section("A profile that cannot be read is refused before anything is copied");

            // The dangerous case: overwriting working settings with a broken file leaves a bot that
            // will not start, which is the opposite of what a profile is for.
            File.WriteAllText(ConfigProfiles.FileFor(folder, "Broken"), "{ this is not json");

            var before = File.ReadAllText(livePath);

            Check("it refuses", Refuses(() => ConfigProfiles.Activate(folder, "Broken")));
            Check("and config.json is exactly as it was", File.ReadAllText(livePath) == before);

            File.WriteAllText(ConfigProfiles.FileFor(folder, "Empty"), "null");
            Check("an empty profile is refused too",
                Refuses(() => ConfigProfiles.Activate(folder, "Empty")));
            Check("config.json still as it was", File.ReadAllText(livePath) == before);

            Check("so is one that is not there",
                Refuses(() => ConfigProfiles.Activate(folder, "never existed")));
            Check("and it is reported as missing rather than as empty or broken",
                Complaint(() => ConfigProfiles.Activate(folder, "never existed"))
                    .Contains("No profile named", StringComparison.Ordinal),
                Complaint(() => ConfigProfiles.Activate(folder, "never existed")));
            Check("and that leaves config.json alone as well", File.ReadAllText(livePath) == before);

            Section("Whether a profile is still the one in effect");

            // Compared as settings, not as text - so the editor can say "active: Campaign" without
            // being thrown off by indentation, property order, or a field one file predates.
            Check("straight after activating, it matches",
                ConfigProfiles.MatchesLiveConfig(folder, "Campaign"));
            Check("a different profile does not",
                !ConfigProfiles.MatchesLiveConfig(folder, "Training"));

            var reformatted = AppConfig.LoadReadOnly(livePath)!.ToComparableJson()
                .Replace("\n", "").Replace("  ", " ");
            File.WriteAllText(livePath, reformatted);
            Check("reformatting config.json does not break the match",
                ConfigProfiles.MatchesLiveConfig(folder, "Campaign"));

            // A config.json from before an update is missing whatever fields the update added; it
            // is still the same settings, and the editor should not report it as edited.
            File.WriteAllText(livePath, "{\"BotCallsign\":\"Magic\"}");
            Check("nor does a config.json that predates a new setting",
                ConfigProfiles.MatchesLiveConfig(folder, "Campaign"));

            File.WriteAllText(livePath, "{\"BotCallsign\":\"Something else\"}");
            Check("but a changed value does",
                !ConfigProfiles.MatchesLiveConfig(folder, "Campaign"));

            File.WriteAllText(livePath, "{ broken");
            Check("an unreadable config.json is not a match, and does not throw",
                Quietly(() => !ConfigProfiles.MatchesLiveConfig(folder, "Campaign")));
            Check("neither is a profile that is not there",
                Quietly(() => !ConfigProfiles.MatchesLiveConfig(folder, "never existed")));
        }
        finally
        {
            Scrap(folder);
        }

        Section("What the editor remembers between launches");

        var settingsFolder = NewFolder();
        try
        {
            var file = Path.Combine(settingsFolder, "sub", "editor.json");

            Check("nothing saved yet is empty rather than an error",
                Quietly(() => EditorSettings.Load(file).ConfigFolder == null));

            var saved = new EditorSettings { ConfigFolder = "somewhere", ActiveProfile = "Training" };
            Check("saving works, folder and all", Quietly(() => saved.Save(file)));
            Check("the file is where it was asked for", File.Exists(file));

            var reloaded = EditorSettings.Load(file);
            Check("the folder comes back", reloaded.ConfigFolder == "somewhere", reloaded.ConfigFolder ?? "null");
            Check("and the active profile", reloaded.ActiveProfile == "Training", reloaded.ActiveProfile ?? "null");

            // Must never take the editor down: this file is a convenience, and a half-written one
            // after a crash would otherwise make the editor unstartable.
            File.WriteAllText(file, "{ not json at all");
            Check("a corrupt file reads as empty instead of throwing",
                Quietly(() => EditorSettings.Load(file).ConfigFolder == null));

            File.WriteAllText(file, "null");
            Check("so does a file holding null",
                Quietly(() => EditorSettings.Load(file).ActiveProfile == null));

            // Saving has to fail quietly too - the editor still works, it just forgets.
            var blocker = Path.Combine(settingsFolder, "blocker");
            File.WriteAllText(blocker, "not a folder");
            Check("a path that cannot be written returns false rather than throwing",
                Quietly(() => !new EditorSettings { ConfigFolder = "x" }.Save(Path.Combine(blocker, "editor.json"))));
        }
        finally
        {
            Scrap(settingsFolder);
        }

        Section("The rules stay platform-independent");

        // The fixed character list above is the entire reason the suite's answers are the same on
        // the machine it runs on and the machine the bot runs on. Guarded so it is not "simplified"
        // back to the framework call, which would quietly make it right here and wrong on Windows.
        // Doc comments stripped first: the file explains at length why it does not call that
        // method, and a check that reads the explanation as a violation would make documenting
        // the decision impossible.
        var source = string.Join("\n", ReadSource("src/Darkstar.Core/ConfigProfiles.cs")
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Check("the forbidden characters are not read off the running platform",
            !source.Contains("GetInvalidFileNameChars", StringComparison.Ordinal));
        Check("and the list is spelled out", source.Contains("'*', '?'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder for the one check that needs a path before the main try/finally starts. Not
    /// created on disk - FileFor never touches it.
    /// </summary>
    private static string NewFolderOnce() => Path.Combine(Path.GetTempPath(), "darkstar-profiles-naming");
}
