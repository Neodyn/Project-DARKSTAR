using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkstar;

/// <summary>One backup file, as it sits in the Backup folder.</summary>
/// <param name="Name">The file name, e.g. <c>Darkstar-Backup-2026-11-17_14-02-11.zip</c>.</param>
/// <param name="LastWriteLocal">Local time, so it reads as "the one from this morning".</param>
/// <param name="BeforeRestore">Made automatically just before a restore - the undo of that restore.</param>
public sealed record BackupFile(string Name, string FullPath, long Bytes, DateTime LastWriteLocal, bool BeforeRestore);

/// <summary>
/// The note every backup carries as <c>darkstar-backup.json</c>: what made it, when, and whether
/// the keys were left out. Nothing about the machine or the person - a backup is a file somebody
/// may send to somebody else.
/// </summary>
public sealed class BackupManifest
{
    /// <summary>The layout of the archive. A restore refuses a format newer than it knows.</summary>
    public int Format { get; set; } = ConfigBackup.Format;

    public DateTime CreatedUtc { get; set; }

    /// <summary>The Darkstar build that made it, as the editor's title bar shows it.</summary>
    public string Build { get; set; } = "";

    /// <summary>
    /// Whether the API keys, the webhook and the password were blanked. A restore of such a
    /// backup keeps the ones already configured instead of blanking them.
    /// </summary>
    public bool SecretsRemoved { get; set; }

    /// <summary>The files in the archive, for somebody looking at it with an unzip tool.</summary>
    public List<string> Files { get; set; } = new();
}

/// <summary>What a backup holds, read and checked but not restored yet.</summary>
/// <param name="Files">The top-level files: config.json, phrases.json, vocabulary.json.</param>
/// <param name="Profiles">Profile names, without ".json".</param>
/// <param name="Ignored">Entries that are not part of a backup and will not be restored.</param>
public sealed record BackupContents(
    BackupManifest Manifest,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Ignored);

/// <summary>What a restore did.</summary>
/// <param name="Restored">Every file written, as a path relative to the config folder.</param>
/// <param name="SafetyBackup">The backup of the state before the restore; empty when there was nothing to back up.</param>
/// <param name="SecretsKept">How many keys were taken from the current settings because the backup had them blanked.</param>
public sealed record RestoreResult(IReadOnlyList<string> Restored, string SafetyBackup, int SecretsKept);

/// <summary>
/// The editor's Backup and Restore: everything the bot is configured with, in one ZIP file.
///
/// A backup holds <c>config.json</c>, <c>phrases.json</c>, <c>vocabulary.json</c> and, by choice,
/// the profiles from <c>Profiles\</c>. The per-file <c>.bak</c> copies <see cref="BackupUtils"/>
/// makes are a different thing: one file at a time, automatic, and only ever next to the file. A
/// backup here is the whole setup at one moment, that can be copied to another PC or kept before a
/// big change.
///
/// Three rules carry the design:
///
/// <b>Nothing is written until everything has been read and checked.</b> Every file in the archive
/// is parsed the way the bot will parse it before the first byte reaches the config folder - a
/// restore that fails half-way would leave a config.json from one day beside a phrases.json from
/// another, which is worse than either.
///
/// <b>A restore can always be undone.</b> Before it writes anything it makes a backup of what is
/// there, named <c>…-before-restore.zip</c>; restoring that one puts everything back.
///
/// <b>The archive decides nothing about paths.</b> Only the three known file names and
/// <c>Profiles/&lt;name&gt;.json</c> with a name <see cref="ConfigProfiles.NameProblem"/> accepts
/// are taken; an entry called <c>..\..\Windows\x</c> is listed as ignored, never written.
/// </summary>
public static class ConfigBackup
{
    /// <summary>The archive layout this version writes and reads.</summary>
    public const int Format = 1;

    /// <summary>
    /// Subfolder of the config folder the backups go to - the same one the automatic <c>.bak</c>
    /// copies use, so there is one place to look for an older state.
    /// </summary>
    public const string FolderName = "Backup";

    public const string FilePrefix = "Darkstar-Backup-";
    public const string BeforeRestoreSuffix = "-before-restore";
    public const string ManifestName = "darkstar-backup.json";

    /// <summary>The files a backup holds besides the profiles, in the order they are restored.</summary>
    public static readonly IReadOnlyList<string> TopFiles = new[] { "phrases.json", "vocabulary.json", "config.json" };

    private const string ProfilePrefix = ConfigProfiles.FolderName + "/";

    /// <summary>
    /// Far above any real config (a few tens of KB). A cap at all because the size an archive
    /// claims for an entry is not checked by anybody but the reader - a small file can unpack to
    /// gigabytes.
    /// </summary>
    public const int MaxEntryBytes = 5 * 1024 * 1024;

    /// <summary>More profiles than anybody keeps; same reason as <see cref="MaxEntryBytes"/>.</summary>
    public const int MaxEntries = 500;

    /// <summary>
    /// The settings that are credentials. Blanked when a backup is made "without keys", and taken
    /// from the current settings when such a backup is restored. A test compares this list with
    /// AppConfig, so a new key cannot be added there and forgotten here.
    /// </summary>
    public static readonly IReadOnlyList<string> SecretFields = new[]
    {
        nameof(AppConfig.GeminiApiKey),
        nameof(AppConfig.DcsGrpcApiKey),
        nameof(AppConfig.DiscordWebhookUrl),
        nameof(AppConfig.ExternalAwacsPassword),
    };

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = false };

    /// <summary>Where the backups of a config folder live.</summary>
    public static string FolderFor(string configFolder) => Path.Combine(configFolder, FolderName);

    /// <summary>The file name a backup made at <paramref name="localTime"/> gets.</summary>
    public static string FileNameFor(DateTime localTime, bool beforeRestore = false) =>
        $"{FilePrefix}{localTime:yyyy-MM-dd_HH-mm-ss}{(beforeRestore ? BeforeRestoreSuffix : "")}.zip";

    // -----------------------------------------------------------------------------------------
    // Making a backup
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a backup of what is on disk in <paramref name="configFolder"/> and returns its path.
    /// </summary>
    /// <param name="target">Where to write it; null puts it into the Backup folder under a dated name.</param>
    /// <param name="removeSecrets">Blank the keys, the webhook and the password - for a copy that leaves the house.</param>
    /// <exception cref="FileNotFoundException">There is no config.json to back up.</exception>
    /// <exception cref="InvalidDataException">Keys were to be removed from a file that is not valid JSON.</exception>
    public static string Create(string configFolder, bool includeProfiles, bool removeSecrets,
        string? target = null, bool beforeRestore = false)
    {
        var configPath = Path.Combine(configFolder, "config.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"There is no config.json in {configFolder} - nothing to back up.", configPath);

        // Everything is read first, so a file that cannot be read stops the backup before a
        // half-written archive exists.
        var entries = new List<(string Name, byte[] Data)>();

        foreach (var name in TopFiles)
        {
            var file = Path.Combine(configFolder, name);
            if (!File.Exists(file)) continue;

            var data = File.ReadAllBytes(file);
            if (removeSecrets && name == "config.json") data = StripSecrets(data, name);
            entries.Add((name, data));
        }

        if (includeProfiles)
        {
            foreach (var profile in ConfigProfiles.List(configFolder))
            {
                // A file somebody dropped into the folder by hand under a name the editor would
                // refuse: it could not be restored either, so it is not backed up as if it could.
                if (ConfigProfiles.NameProblem(profile.Name) != null) continue;

                var data = File.ReadAllBytes(profile.FullPath);
                if (removeSecrets) data = StripSecrets(data, ProfilePrefix + profile.Name + ".json");
                entries.Add((ProfilePrefix + profile.Name + ".json", data));
            }
        }

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            Build = VersionInfo.Display,
            SecretsRemoved = removeSecrets,
            Files = entries.Select(e => e.Name).ToList(),
        };

        var path = target ?? UniquePath(FolderFor(configFolder), FileNameFor(DateTime.Now, beforeRestore));
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // Written beside the target and moved into place: a full disk or a crash leaves a .tmp
        // file, never a backup that looks complete and is not.
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add(zip, ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson));
                foreach (var (name, data) in entries) Add(zip, name, data);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }

        return path;
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(data, 0, data.Length);
    }

    /// <summary>A path in that folder that is not taken yet - two backups in one second keep both.</summary>
    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem}-{n}.zip");

        return path;
    }

    /// <summary>
    /// The JSON with every credential blanked. Works on the JSON itself rather than through
    /// <see cref="AppConfig"/>, so the file keeps exactly the fields it had - a backup is a copy,
    /// not a rewrite with whatever defaults this version would add.
    /// </summary>
    internal static byte[] StripSecrets(byte[] json, string nameForMessage)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(WithoutBom(json), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{nameForMessage} is not valid JSON, so its keys cannot be removed ({ex.Message}). " +
                "Fix the file, or make the backup with the keys in it.", ex);
        }

        if (root is not JsonObject settings) return json;

        foreach (var property in settings.Select(p => p.Key).ToList())
        {
            if (!SecretFields.Contains(property, StringComparer.OrdinalIgnoreCase)) continue;
            if (settings[property] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                settings[property] = "";
        }

        return Encoding.UTF8.GetBytes(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The bytes without a UTF-8 byte order mark. Notepad writes one, and the JSON reader in
    /// <see cref="JsonNode"/> stops at it.
    /// </summary>
    private static ReadOnlySpan<byte> WithoutBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsSpan(3)
            : bytes;

    // -----------------------------------------------------------------------------------------
    // Listing and reading
    // -----------------------------------------------------------------------------------------

    /// <summary>The backups in the Backup folder, newest first. No folder is no backups.</summary>
    public static List<BackupFile> List(string configFolder)
    {
        var folder = FolderFor(configFolder);
        if (!Directory.Exists(folder)) return new List<BackupFile>();

        return Directory.EnumerateFiles(folder, FilePrefix + "*.zip")
            .Select(path => new FileInfo(path))
            .Select(info => new BackupFile(
                info.Name, info.FullName, info.Length, info.LastWriteTime,
                info.Name.Contains(BeforeRestoreSuffix, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(b => b.LastWriteLocal)
            .ThenByDescending(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads a backup and checks every file in it, without writing anything.
    /// </summary>
    /// <exception cref="InvalidDataException">It is not a backup this version can restore; the message says why.</exception>
    public static BackupContents Inspect(string zipPath) => Read(zipPath).Contents;

    /// <summary>The checked contents together with the bytes to restore, keyed by entry name.</summary>
    private static (BackupContents Contents, Dictionary<string, byte[]> Data) Read(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new InvalidDataException($"There is no file {zipPath}.");

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException($"{Path.GetFileName(zipPath)} is not a ZIP file.");
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
                throw new InvalidDataException($"The archive holds {zip.Entries.Count} entries; a backup has a few dozen at most.");

            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var profiles = new List<string>();
            var ignored = new List<string>();
            BackupManifest? manifest = null;

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue; // a folder entry, nothing to restore

                var known = TopFiles.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
                var profile = ProfileName(name);

                if (string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    manifest = ParseManifest(ReadEntry(entry, name));
                    continue;
                }

                if (known == null && profile == null)
                {
                    ignored.Add(name);
                    continue;
                }

                var key = known ?? ProfilePrefix + profile + ".json";
                if (data.ContainsKey(key))
                    throw new InvalidDataException($"The archive holds {key} twice - which one is meant cannot be told.");

                var bytes = ReadEntry(entry, name);
                Validate(key, bytes);
                data[key] = bytes;
                if (profile != null) profiles.Add(profile);
            }

            if (!data.ContainsKey("config.json"))
                throw new InvalidDataException("The archive holds no config.json - it is not a Darkstar backup.");

            // An archive without the note is still restorable - somebody zipped the three files by
            // hand - it just cannot say when it was made or whether the keys are in it.
            manifest ??= new BackupManifest { Format = 0 };

            if (manifest.Format > Format)
                throw new InvalidDataException(
                    $"This backup was made by a newer Darkstar (format {manifest.Format}, this one reads {Format}). Update the editor first.");

            var files = TopFiles.Where(data.ContainsKey).ToList();
            profiles.Sort(StringComparer.OrdinalIgnoreCase);

            return (new BackupContents(manifest, files, profiles, ignored), data);
        }
    }

    /// <summary>
    /// The profile name an entry stands for, or null when it is not a profile this version would
    /// write: exactly one level under Profiles/, a .json file, a name the profile rules accept.
    /// </summary>
    internal static string? ProfileName(string entryName)
    {
        if (!entryName.StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = entryName[ProfilePrefix.Length..];
        if (rest.Contains('/') || rest.Contains('\\')) return null;
        if (!rest.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;

        var name = rest[..^".json".Length];
        return ConfigProfiles.NameProblem(name) == null && ConfigProfiles.Normalize(name) == name ? name : null;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, string name)
    {
        if (entry.Length > MaxEntryBytes)
            throw new InvalidDataException($"{name} in the backup is {entry.Length / 1024 / 1024} MB - far too large for a settings file.");

        // The length above is what the archive claims. Reading stops at the limit whatever it says.
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxEntryBytes)
                throw new InvalidDataException($"{name} in the backup unpacks to more than {MaxEntryBytes / 1024 / 1024} MB.");
        }

        return buffer.ToArray();
    }

    private static BackupManifest ParseManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(bytes, ManifestJson) ?? new BackupManifest { Format = 0 };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ManifestName} in the backup is broken ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Parses a file the way the bot will. Anything the bot would refuse or silently drop is
    /// refused here, before it replaces a file the bot can read.
    /// </summary>
    private static void Validate(string name, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        try
        {
            object? parsed = name switch
            {
                "phrases.json" => JsonSerializer.Deserialize<List<PhraseEntry>>(text, ReadJson),
                "vocabulary.json" => JsonSerializer.Deserialize<List<string>>(text, ReadJson),
                _ => AppConfig.FromJson(text),
            };

            if (parsed == null)
                throw new InvalidDataException($"{name} in the backup is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name} in the backup is not valid ({ex.Message}). Nothing was restored.", ex);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Restoring
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Restores a backup into <paramref name="configFolder"/>: the three files and the profiles
    /// it holds. Profiles that are not in the backup are left alone - a restore brings settings
    /// back, it does not delete work done since.
    ///
    /// The whole archive is checked first and the current state is backed up second; only then is
    /// anything written. Either step failing throws, with nothing changed.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive cannot be restored; nothing was written.</exception>
    public static RestoreResult Restore(string configFolder, string zipPath)
    {
        if (!Directory.Exists(configFolder))
            throw new DirectoryNotFoundException($"There is no folder {configFolder}.");

        var (contents, data) = Read(zipPath);

        var safety = File.Exists(Path.Combine(configFolder, "config.json"))
            ? Create(configFolder, includeProfiles: true, removeSecrets: false, beforeRestore: true)
            : "";

        var keptSecrets = 0;
        var restored = new List<string>();

        // Profiles first and config.json last: a bot or editor that reads in between sees the
        // old config.json beside the new phrases, never the other way round, and config.json is
        // the file that decides whether the bot starts at all.
        var order = data.Keys
            .Where(k => k.StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Concat(TopFiles.Where(data.ContainsKey));

        foreach (var name in order)
        {
            var profile = ProfileName(name);
            var target = profile != null
                ? ConfigProfiles.FileFor(configFolder, profile)
                : Path.Combine(configFolder, name);

            var bytes = data[name];

            if (contents.Manifest.SecretsRemoved && (profile != null || name == "config.json"))
                bytes = KeepSecrets(bytes, target, ref keptSecrets);

            WriteReplacing(target, bytes);
            restored.Add(profile != null ? Path.Combine(ConfigProfiles.FolderName, profile + ".json") : name);
        }

        return new RestoreResult(restored, safety, keptSecrets);
    }

    /// <summary>
    /// For a backup made without keys: every credential that is blank in the backup is taken from
    /// the file it is about to replace. Restoring a shared copy must not cost the operator their
    /// API key.
    /// </summary>
    internal static byte[] KeepSecrets(byte[] restored, string currentPath, ref int kept)
    {
        if (!File.Exists(currentPath)) return restored;

        JsonObject? current;
        JsonObject? incoming;
        try
        {
            current = JsonNode.Parse(WithoutBom(File.ReadAllBytes(currentPath))) as JsonObject;
            incoming = JsonNode.Parse(WithoutBom(restored)) as JsonObject;
        }
        catch (JsonException)
        {
            // The current file is broken: there is nothing to take from it. The backup itself was
            // already parsed, so this is about the file being replaced.
            return restored;
        }

        if (current == null || incoming == null) return restored;

        var changed = false;
        foreach (var field in SecretFields)
        {
            var have = current.FirstOrDefault(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase));
            if (have.Value is not JsonValue haveValue || !haveValue.TryGetValue<string>(out var secret) || secret.Length == 0)
                continue;

            var key = incoming.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)) ?? field;
            var isBlank = incoming[key] is null
                          || (incoming[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);
            if (!isBlank) continue;

            incoming[key] = secret;
            kept++;
            changed = true;
        }

        return changed
            ? Encoding.UTF8.GetBytes(incoming.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
            : restored;
    }

    /// <summary>Writes a file through a temporary one, with the usual .bak of what it replaces.</summary>
    private static void WriteReplacing(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        BackupUtils.BackupBeforeWrite(path);

        var temp = path + ".restore.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
