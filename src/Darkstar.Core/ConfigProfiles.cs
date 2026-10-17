namespace Darkstar;

/// <summary>One saved profile, as it sits in the Profiles folder.</summary>
/// <param name="Name">What the editor shows and what the file is called, without ".json".</param>
/// <param name="LastWriteLocal">Local time, so it compares against "I saved that one last week".</param>
public sealed record ConfigProfile(string Name, string FullPath, long Bytes, DateTime LastWriteLocal);

/// <summary>
/// Several named <c>config.json</c> files kept side by side, so one installation can be switched
/// between setups - a training server and a campaign server, AWACS-only and towers-as-well, the
/// settings for a mission that needs a different frequency plan.
///
/// A profile is a whole <c>config.json</c> under <c>&lt;config folder&gt;\Profiles\&lt;name&gt;.json</c>.
/// Activating one copies it over the <c>config.json</c> the bot reads, which is the only file the
/// bot ever looks at - deliberately, because the alternative is teaching the bot about profiles
/// and giving a running service a second way to be pointed at the wrong settings. The bot stays
/// as simple as it was; the editor does the copying.
///
/// <c>phrases.json</c> and <c>vocabulary.json</c> are NOT part of a profile. They are shared
/// across all of them, and the editor says so, because silently swapping the fixed phrases along
/// with the frequency plan is the kind of surprise that gets found during a mission.
///
/// The name rules below are the substance of this class. A profile name becomes a file name, so
/// "..\config" would write outside the folder and <c>CON</c> is not a file on Windows at all -
/// both are refused here rather than discovered as a corrupt installation later.
/// </summary>
public static class ConfigProfiles
{
    /// <summary>Subfolder of the config folder that holds the saved profiles.</summary>
    public const string FolderName = "Profiles";

    /// <summary>
    /// Long enough for "Campaign - towers and tanker", short enough to stay inside the path
    /// length a deep install folder leaves over.
    /// </summary>
    public const int MaxNameLength = 60;

    /// <summary>
    /// Characters a profile name may not contain - the Windows set, applied on every platform
    /// rather than asking <c>Path.GetInvalidFileNameChars()</c>.
    ///
    /// That method answers for the machine it runs on: on Linux it returns the separator and NUL
    /// and nothing else, so a name containing a colon would be accepted by a check written this
    /// way and then refused by the only operating system this bot runs on. A fixed list behaves
    /// the same everywhere, which is also what makes it testable.
    /// </summary>
    private static readonly char[] Forbidden = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>
    /// Names Windows reserves for devices. <c>CON.json</c> is the console, not a file, and a
    /// profile called CON therefore cannot be saved, read or deleted - it just fails, with an
    /// error message about the file system rather than about the name.
    /// </summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Where the profiles live for a given config folder.</summary>
    public static string FolderFor(string configFolder) => Path.Combine(configFolder, FolderName);

    /// <summary>
    /// The name with surrounding whitespace removed - what gets stored and compared. Typing a
    /// trailing space is not a different profile.
    /// </summary>
    public static string Normalize(string? name) => (name ?? "").Trim();

    /// <summary>
    /// What is wrong with this name, as a sentence for the operator, or null when it is usable.
    ///
    /// Returns the problem rather than a bool so the editor can say which character it objected
    /// to - "that name contains a \" is actionable where "invalid name" is not.
    /// </summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = Normalize(name);

        if (trimmed.Length == 0)
            return "A profile needs a name.";

        if (trimmed.Length > MaxNameLength)
            return $"That name is {trimmed.Length} characters; {MaxNameLength} is the limit.";

        // Checked before the character list, so "../other" is answered with what it would
        // actually do rather than with a complaint about a slash.
        if (trimmed == "." || trimmed == ".." || trimmed.Contains("..", StringComparison.Ordinal))
            return "A name cannot contain \"..\" - a profile stays inside the Profiles folder.";

        var offending = trimmed.FirstOrDefault(c => Forbidden.Contains(c));
        if (offending != default)
            return $"A name cannot contain {offending} - it becomes a file name.";

        if (trimmed.Any(char.IsControl))
            return "A name cannot contain control characters.";

        // Windows silently drops a trailing dot when creating the file, so the profile would come
        // back under a name nobody typed and the editor would look like it had lost it.
        //
        // A trailing space does the same thing on Windows but cannot reach here: Normalize trims,
        // and Trim() removes every Unicode space including the non-breaking one. Checking for it
        // anyway was dead code that read like a guarantee.
        if (trimmed.EndsWith('.'))
            return "A name cannot end with a dot - Windows would drop it.";

        var withoutExtension = trimmed.Contains('.', StringComparison.Ordinal)
            ? trimmed[..trimmed.IndexOf('.', StringComparison.Ordinal)]
            : trimmed;

        if (ReservedNames.Contains(withoutExtension, StringComparer.OrdinalIgnoreCase))
            return $"\"{withoutExtension}\" is a name Windows reserves for a device; pick another.";

        return null;
    }

    /// <summary>
    /// The file a profile name maps to.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is not usable. Thrown rather than sanitized: quietly turning "..\x" into
    /// something writable is how a path traversal becomes a feature.
    /// </exception>
    public static string FileFor(string configFolder, string? name)
    {
        var problem = NameProblem(name);
        if (problem != null) throw new ArgumentException(problem, nameof(name));

        return Path.Combine(FolderFor(configFolder), Normalize(name) + ".json");
    }

    /// <summary>The profile name a file holds, i.e. its name without the ".json".</summary>
    public static string NameOf(string filePath) => Path.GetFileNameWithoutExtension(filePath);

    /// <summary>
    /// The saved profiles, by name. An absent folder is no profiles rather than an error - it is
    /// what every installation looks like until the first one is saved.
    /// </summary>
    public static List<ConfigProfile> List(string configFolder)
    {
        var folder = FolderFor(configFolder);
        if (!Directory.Exists(folder)) return new List<ConfigProfile>();

        return Directory.EnumerateFiles(folder, "*.json")
            .Select(path => new FileInfo(path))
            .Select(info => new ConfigProfile(NameOf(info.FullName), info.FullName, info.Length, info.LastWriteTime))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether a profile of that name already exists, for the editor's overwrite prompt.</summary>
    public static bool Exists(string configFolder, string? name) =>
        NameProblem(name) == null && File.Exists(FileFor(configFolder, name));

    /// <summary>
    /// Writes the given settings to the profile of that name, creating the folder on the way and
    /// backing up a profile that is being replaced.
    /// </summary>
    public static void Save(string configFolder, string? name, AppConfig config)
    {
        var path = FileFor(configFolder, name);
        Directory.CreateDirectory(FolderFor(configFolder));
        config.Save(path);
    }

    /// <summary>
    /// Copies a profile over the <c>config.json</c> the bot reads, keeping a backup of the
    /// config that was there.
    ///
    /// The profile is parsed first. A profile that cannot be read is refused here, with
    /// <c>config.json</c> untouched - overwriting working settings with a broken file would leave
    /// a bot that no longer starts, and the one thing the operator wanted was a way back.
    /// </summary>
    /// <exception cref="FileNotFoundException">No profile of that name.</exception>
    /// <exception cref="System.Text.Json.JsonException">The profile is not valid JSON.</exception>
    public static void Activate(string configFolder, string? name)
    {
        var source = FileFor(configFolder, name);

        if (!File.Exists(source))
            throw new FileNotFoundException($"No profile named \"{Normalize(name)}\".", source);

        // Throws on bad JSON, which is the point of reading it at all.
        if (AppConfig.LoadReadOnly(source) == null)
            throw new System.Text.Json.JsonException($"The profile \"{Normalize(name)}\" is empty.");

        var target = Path.Combine(configFolder, "config.json");
        BackupUtils.BackupBeforeWrite(target);
        File.Copy(source, target, overwrite: true);
    }

    /// <summary>
    /// Deletes a profile, after a backup. Deleting the wrong one is a plain mistake rather than a
    /// recoverable edit, so the copy in Backup\ is the undo.
    /// </summary>
    /// <exception cref="FileNotFoundException">No profile of that name.</exception>
    public static void Delete(string configFolder, string? name)
    {
        var path = FileFor(configFolder, name);

        if (!File.Exists(path))
            throw new FileNotFoundException($"No profile named \"{Normalize(name)}\".", path);

        BackupUtils.BackupBeforeWrite(path);
        File.Delete(path);
    }

    /// <summary>
    /// Whether the live <c>config.json</c> still holds what that profile holds - so the editor can
    /// say "active: Training" against "active: Training, with unsaved changes" instead of claiming
    /// a profile is in effect after somebody has edited the settings.
    ///
    /// Compared as settings rather than as text: both files are read into an <see cref="AppConfig"/>
    /// and written back out with the same serializer, so indentation, property order and a field
    /// one of the two files is missing make no difference, while any value that differs does.
    /// </summary>
    public static bool MatchesLiveConfig(string configFolder, string? name)
    {
        try
        {
            var profile = AppConfig.LoadReadOnly(FileFor(configFolder, name));
            var live = AppConfig.LoadReadOnly(Path.Combine(configFolder, "config.json"));

            if (profile == null || live == null) return false;

            return profile.ToComparableJson() == live.ToComparableJson();
        }
        catch (Exception)
        {
            // An unreadable or unparsable file is not a match, and this question is never
            // important enough to fail the editor over.
            return false;
        }
    }
}
