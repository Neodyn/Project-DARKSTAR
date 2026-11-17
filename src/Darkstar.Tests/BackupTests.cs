using System.IO.Compression;
using System.Reflection;
using System.Text;
using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The editor's backup and restore (<see cref="ConfigBackup"/>).
///
/// A restore overwrites the settings a working bot reads, so most of this file is about the cases
/// where it must NOT: an archive that is broken, made by a newer version, holding a file the bot
/// would refuse, or naming a path outside the config folder. Each of those is checked by what is
/// on disk afterwards - config.json unchanged, no "before restore" file - rather than by whether
/// an exception came out.
/// </summary>
internal static class BackupTests
{
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "darkstar-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Scrap(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
    }

    private static string Complaint(Action call)
    {
        try { call(); return "(it did not refuse at all)"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>A config folder as the bot leaves it: three files and two profiles.</summary>
    private static string Setup()
    {
        var folder = NewFolder();
        new AppConfig { BotCallsign = "Magic", GeminiApiKey = "key-one", DiscordWebhookUrl = "https://hook" }
            .Save(Path.Combine(folder, "config.json"));
        PhraseBook.Save(Path.Combine(folder, "phrases.json"),
            new List<PhraseEntry> { new() { Trigger = "status", Response = "All good." } });
        VocabularyBook.Save(Path.Combine(folder, "vocabulary.json"), new List<string> { "Viggen" });
        ConfigProfiles.Save(folder, "Training", new AppConfig { BotCallsign = "Trainer", GeminiApiKey = "key-training" });
        ConfigProfiles.Save(folder, "Campaign", new AppConfig { BotCallsign = "Darkstar" });
        return folder;
    }

    private static AppConfig Live(string folder) => AppConfig.LoadReadOnly(Path.Combine(folder, "config.json"))!;

    private static string EntryText(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.GetEntry(name);
        if (entry == null) return "";
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>A hand-made archive for the refusal cases.</summary>
    private static string Archive(string folder, string fileName, params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(folder, fileName);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }

    public static void Run()
    {
        var goodConfig = new AppConfig().ToComparableJson();

        Section("A backup holds the whole setup, and gives it back");

        var folder = Setup();
        try
        {
            var zip = ConfigBackup.Create(folder, includeProfiles: true, removeSecrets: false);

            Check("it is written into the Backup folder",
                Path.GetDirectoryName(zip) == ConfigBackup.FolderFor(folder), zip);
            Check("under a dated name", Path.GetFileName(zip).StartsWith(ConfigBackup.FilePrefix, StringComparison.Ordinal)
                                        && zip.EndsWith(".zip", StringComparison.Ordinal), Path.GetFileName(zip));
            Check("and no temporary file is left beside it", !File.Exists(zip + ".tmp"));

            var contents = ConfigBackup.Inspect(zip);
            Eq("it holds the three files", string.Join(",", contents.Files), "phrases.json,vocabulary.json,config.json");
            Eq("and both profiles", string.Join(",", contents.Profiles), "Campaign,Training");
            Check("with a note saying what it is", contents.Manifest.Format == ConfigBackup.Format && !contents.Manifest.SecretsRemoved);
            // Names shorter than four letters are skipped: "ed" occurs in "CreatedUtc", and that
            // is not the manifest naming anybody.
            var note = EntryText(zip, ConfigBackup.ManifestName);
            bool Names(string who) => who.Length >= 4 && note.Contains(who, StringComparison.OrdinalIgnoreCase);
            Check("the note names no machine and no person",
                !Names(Environment.MachineName) && !Names(Environment.UserName), note.Replace("\n", " "));
            Check("the keys are in it, as asked", EntryText(zip, "config.json").Contains("key-one", StringComparison.Ordinal));

            var listed = ConfigBackup.List(folder);
            Check("the Backup folder lists it", listed.Count == 1 && listed[0].FullPath == zip && !listed[0].BeforeRestore,
                string.Join(", ", listed.Select(b => b.Name)));

            // What happens after the backup: a change, a deleted profile, a new one.
            new AppConfig { BotCallsign = "Changed", GeminiApiKey = "key-two" }.Save(Path.Combine(folder, "config.json"));
            ConfigProfiles.Delete(folder, "Training");
            ConfigProfiles.Save(folder, "Later", new AppConfig { BotCallsign = "Later" });

            Check("the .bak copy of a saved config.json shares the folder, and is not listed as a backup",
                Directory.GetFiles(ConfigBackup.FolderFor(folder), "*.bak").Length > 0
                && ConfigBackup.List(folder).Count == 1);

            var result = ConfigBackup.Restore(folder, zip);

            Eq("config.json is the one from the backup", Live(folder).BotCallsign, "Magic");
            Eq("keys and all", Live(folder).GeminiApiKey, "key-one");
            Check("the deleted profile is back", ConfigProfiles.Exists(folder, "Training"));
            Check("a profile made since is left alone", ConfigProfiles.Exists(folder, "Later"));
            Check("phrases and vocabulary are restored too",
                result.Restored.Contains("phrases.json") && result.Restored.Contains("vocabulary.json"),
                string.Join(", ", result.Restored));
            Check("config.json is written last, so nothing reads new phrases beside an old config",
                result.Restored.Last() == "config.json", string.Join(", ", result.Restored));

            Check("the state before was backed up first",
                File.Exists(result.SafetyBackup) && result.SafetyBackup.Contains(ConfigBackup.BeforeRestoreSuffix, StringComparison.Ordinal),
                result.SafetyBackup);
            Check("and is marked as such in the list",
                ConfigBackup.List(folder).Count(b => b.BeforeRestore) == 1);

            ConfigBackup.Restore(folder, result.SafetyBackup);
            Eq("restoring that one undoes the restore", Live(folder).BotCallsign, "Changed");
            Eq("its key included", Live(folder).GeminiApiKey, "key-two");

            var second = ConfigBackup.Create(folder, includeProfiles: false, removeSecrets: false);
            var third = ConfigBackup.Create(folder, includeProfiles: false, removeSecrets: false);
            Check("two backups in the same second keep both", second != third && File.Exists(second) && File.Exists(third),
                $"{Path.GetFileName(second)} / {Path.GetFileName(third)}");
            Check("without the profiles when asked", ConfigBackup.Inspect(second).Profiles.Count == 0);

            var elsewhere = Path.Combine(folder, "usb", "copy.zip");
            Check("and anywhere else when a file is given",
                ConfigBackup.Create(folder, true, false, elsewhere) == elsewhere && File.Exists(elsewhere));
        }
        finally
        {
            Scrap(folder);
        }

        Section("A copy without keys does not cost anybody their keys");

        folder = Setup();
        try
        {
            var zip = ConfigBackup.Create(folder, includeProfiles: true, removeSecrets: true);
            var config = EntryText(zip, "config.json");

            Check("the API key is blanked", !config.Contains("key-one", StringComparison.Ordinal));
            Check("so is the webhook", !config.Contains("https://hook", StringComparison.Ordinal));
            Check("and the profiles' keys", !EntryText(zip, "Profiles/Training.json").Contains("key-training", StringComparison.Ordinal));
            Check("everything else is kept", config.Contains("Magic", StringComparison.Ordinal));
            Check("the note says so", ConfigBackup.Inspect(zip).Manifest.SecretsRemoved);

            new AppConfig { BotCallsign = "Other", GeminiApiKey = "key-here" }.Save(Path.Combine(folder, "config.json"));
            var result = ConfigBackup.Restore(folder, zip);

            Eq("restoring it brings the settings back", Live(folder).BotCallsign, "Magic");
            Eq("but keeps the key configured here", Live(folder).GeminiApiKey, "key-here");
            Check("and says how many it kept", result.SecretsKept >= 1, result.SecretsKept.ToString());

            var withKeys = ConfigBackup.Create(folder, includeProfiles: false, removeSecrets: false);
            new AppConfig { GeminiApiKey = "key-later" }.Save(Path.Combine(folder, "config.json"));
            ConfigBackup.Restore(folder, withKeys);
            Eq("a backup WITH keys does replace them", Live(folder).GeminiApiKey, "key-here");

            // Notepad saves with a byte order mark; the keys still have to come out.
            var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{ \"GeminiApiKey\": \"secret\", \"BotCallsign\": \"X\" }")).ToArray();
            var stripped = Encoding.UTF8.GetString(ConfigBackup.StripSecrets(bom, "config.json"));
            Check("a file with a byte order mark is stripped too",
                !stripped.Contains("secret", StringComparison.Ordinal) && stripped.Contains("\"X\"", StringComparison.Ordinal), stripped);

            // Every credential AppConfig has must be on the list - one added later and forgotten
            // here would ride along in every "without keys" copy.
            var credentialLike = typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => p.Name)
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, "Key$|Password|Webhook|Token|Secret"))
                .ToList();
            var forgotten = credentialLike.Except(ConfigBackup.SecretFields).ToList();
            Check($"every credential setting is on the list ({credentialLike.Count} found)", forgotten.Count == 0,
                string.Join(", ", forgotten));
            Check("and the list names only settings that exist",
                ConfigBackup.SecretFields.All(f => typeof(AppConfig).GetProperty(f) != null));
        }
        finally
        {
            Scrap(folder);
        }

        Section("A backup that cannot be restored changes nothing");

        folder = Setup();
        try
        {
            var before = File.ReadAllText(Path.Combine(folder, "config.json"));
            var scratch = NewFolder();

            void Refused(string name, string zip, string expectInMessage)
            {
                var message = Complaint(() => ConfigBackup.Restore(folder, zip));
                Check($"{name}: refused", message.Contains(expectInMessage, StringComparison.OrdinalIgnoreCase), message);
                Check($"{name}: config.json untouched", File.ReadAllText(Path.Combine(folder, "config.json")) == before);
                Check($"{name}: and nothing was backed up, because nothing happened",
                    ConfigBackup.List(folder).All(b => !b.BeforeRestore));
            }

            var notZip = Path.Combine(scratch, "notes.zip");
            File.WriteAllText(notZip, "this is not an archive");
            Refused("a file that is not a ZIP", notZip, "not a ZIP");

            Refused("an archive without config.json",
                Archive(scratch, "empty.zip", ("phrases.json", "[]")), "no config.json");

            Refused("a config.json the bot could not read",
                Archive(scratch, "broken.zip", ("config.json", "{ \"BotCallsign\": ")), "not valid");

            Refused("phrases that are not a list of phrases",
                Archive(scratch, "phrases.zip", ("config.json", goodConfig), ("phrases.json", "{ \"a\": 1 }")), "phrases.json");

            Refused("a backup from a newer version",
                Archive(scratch, "future.zip", ("config.json", goodConfig), (ConfigBackup.ManifestName, "{ \"Format\": 99 }")), "newer");

            Refused("config.json twice, in two spellings",
                Archive(scratch, "twice.zip", ("config.json", goodConfig), ("Config.json", goodConfig)), "twice");

            Refused("an entry that unpacks to more than any settings file",
                Archive(scratch, "huge.zip", ("config.json", goodConfig), ("vocabulary.json", "[\"" + new string('a', ConfigBackup.MaxEntryBytes + 10) + "\"]")),
                "MB");

            // Paths: the archive never decides where something is written.
            var sneaky = Archive(scratch, "sneaky.zip",
                ("config.json", goodConfig),
                ("../escaped.json", "{}"),
                ("Profiles/../escaped2.json", "{}"),
                ("Profiles/deeper/x.json", goodConfig),
                ("Profiles/CON.json", goodConfig),
                ("C:/Windows/evil.json", "{}"));

            var contents = ConfigBackup.Inspect(sneaky);
            Check("entries outside the known names are listed as ignored", contents.Ignored.Count == 5,
                string.Join(", ", contents.Ignored));
            Check("and none of them counts as a profile", contents.Profiles.Count == 0);

            ConfigBackup.Restore(folder, sneaky);
            Check("restoring it writes nothing outside the config folder",
                !File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "escaped.json"))
                && !File.Exists(Path.Combine(folder, "escaped2.json"))
                && !Directory.Exists(Path.Combine(ConfigProfiles.FolderFor(folder), "deeper")));

            var handMade = Archive(scratch, "hand.zip", ("config.json", goodConfig), ("Profiles/Spare.json", goodConfig));
            var handContents = ConfigBackup.Inspect(handMade);
            Check("an archive somebody zipped by hand is still accepted", handContents.Manifest.Format == 0
                                                                           && handContents.Profiles.SequenceEqual(new[] { "Spare" }));

            Scrap(scratch);
        }
        finally
        {
            Scrap(folder);
        }

        Section("Profile names inside an archive follow the profile rules");

        Eq("a plain profile", ConfigBackup.ProfileName("Profiles/Training.json") ?? "null", "Training");
        Eq("whatever the case of the folder", ConfigBackup.ProfileName("profiles/Training.json") ?? "null", "Training");
        Eq("a step out of the folder", ConfigBackup.ProfileName("Profiles/../x.json") ?? "null", "null");
        Eq("a subfolder", ConfigBackup.ProfileName("Profiles/a/b.json") ?? "null", "null");
        Eq("a device name", ConfigBackup.ProfileName("Profiles/CON.json") ?? "null", "null");
        Eq("not a .json file", ConfigBackup.ProfileName("Profiles/notes.txt") ?? "null", "null");
        Eq("a name with a space around it", ConfigBackup.ProfileName("Profiles/ x .json") ?? "null", "null");

        Section("Nothing to back up is said, not guessed at");

        var emptyFolder = NewFolder();
        try
        {
            var message = Complaint(() => ConfigBackup.Create(emptyFolder, true, false));
            Check("a folder without config.json is refused", message.Contains("no config.json", StringComparison.Ordinal), message);
            Check("and no archive is left behind", !Directory.Exists(ConfigBackup.FolderFor(emptyFolder)));
            Check("an absent Backup folder lists as empty", ConfigBackup.List(emptyFolder).Count == 0);
        }
        finally
        {
            Scrap(emptyFolder);
        }

        Section("The editor asks before it restores");

        var panel = ReadSource("src/Darkstar.Gui/Pages/ProfilesPanel.razor");
        Check("picking a backup only reads it",
            panel.Contains("_candidateContents = Store.InspectBackup(path, out _candidateProblem);", StringComparison.Ordinal));
        Check("restoring needs the answer to that question",
            panel.Contains("if (_candidate == null || _candidateContents == null) return;", StringComparison.Ordinal));

        var store = ReadSource("src/Darkstar.Gui/ConfigStore.cs");
        var restoreAt = store.IndexOf("ConfigBackup.Restore(ConfigFolder, path)", StringComparison.Ordinal);
        var reloadAt = restoreAt < 0 ? -1 : store.IndexOf("Load();", restoreAt, StringComparison.Ordinal);
        Check("and reloads afterwards, so the screen shows what the bot will read", restoreAt > 0 && reloadAt > restoreAt);
    }
}
