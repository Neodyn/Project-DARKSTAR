namespace Darkstar;

/// <summary>
/// Finds the DCS-SimpleRadio-Standalone installation, specifically DCS-SR-ExternalAudio.exe -
/// the tool the bot shells out to for every reply it transmits.
///
/// SRS ships that executable in an "ExternalAudio" subfolder of its install directory
/// (C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio\DCS-SR-ExternalAudio.exe on a
/// default install), but older builds and the server-only package put it elsewhere, and plenty
/// of people install SRS to a different drive. So instead of hard-coding one path, everything
/// that needs it probes a list of known layouts under a list of known roots.
///
/// This lives in Darkstar.Core (plain net8.0, no registry dependency) so the bot, the config
/// editor and the unit tests all share one definition of "where SRS might be". The Windows
/// installer does the same probing in Pascal script, plus a registry lookup of SRS's own
/// uninstall entry, which this cannot do without pulling in an extra package.
/// </summary>
public static class SrsPaths
{
    /// <summary>The executable's file name - the one thing that is stable across SRS versions.</summary>
    public const string ExternalAudioExeName = "DCS-SR-ExternalAudio.exe";

    /// <summary>
    /// Where a default SRS install puts the executable. Used as the config default so a fresh
    /// config.json is right for the common case even when nothing detected anything.
    /// </summary>
    public const string DefaultExternalAudioExePath =
        @"C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio\DCS-SR-ExternalAudio.exe";

    /// <summary>
    /// Folder names SRS installs itself under, most likely first. Probed below every search root.
    /// </summary>
    internal static readonly string[] InstallFolderNames =
    {
        "DCS-SimpleRadio-Standalone",
        "DCS-SimpleRadio-Standalone-Server",
        "DCS-SimpleRadio",
        "SimpleRadio-Standalone",
    };

    /// <summary>
    /// Where the executable sits inside an SRS install folder, most likely first. Kept as path
    /// segments rather than backslash strings so Path.Combine produces valid paths on any OS -
    /// which is what lets the unit tests build fake install trees on Linux.
    /// </summary>
    internal static readonly string[][] ExeRelativePaths =
    {
        new[] { "ExternalAudio", ExternalAudioExeName },
        new[] { ExternalAudioExeName },
        new[] { "Server", "ExternalAudio", ExternalAudioExeName },
        new[] { "Server", ExternalAudioExeName },
        new[] { "Client", "ExternalAudio", ExternalAudioExeName },
    };

    /// <summary>
    /// Finds DCS-SR-ExternalAudio.exe on this machine, or null if no known layout matched.
    /// Never throws: an unreadable or missing root is simply skipped.
    /// </summary>
    public static string? FindExternalAudioExe() =>
        FindExternalAudioExe(DefaultSearchRoots(), File.Exists, Directory.Exists);

    /// <summary>
    /// Testable core of <see cref="FindExternalAudioExe()"/>: probes the given roots with the
    /// given existence checks and returns the first hit.
    /// </summary>
    /// <param name="searchRoots">
    /// Folders that may contain an SRS install folder. Each root is also probed directly, so
    /// passing the SRS install folder itself (or the folder holding the exe) works too.
    /// </param>
    internal static string? FindExternalAudioExe(
        IEnumerable<string> searchRoots,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        foreach (var candidate in EnumerateCandidates(searchRoots, directoryExists))
        {
            if (SafeCheck(fileExists, candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Turns whatever the user typed into a usable path to the executable: a folder (the SRS
    /// install folder, or the ExternalAudio folder itself) is resolved to the executable inside
    /// it; a path that already points at the executable is returned unchanged. Returns null when
    /// nothing was found, so the caller can keep the user's original text rather than blanking it.
    /// </summary>
    public static string? ResolveToExecutable(string? pathOrFolder) =>
        ResolveToExecutable(pathOrFolder, File.Exists, Directory.Exists);

    internal static string? ResolveToExecutable(
        string? pathOrFolder,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(pathOrFolder))
            return null;

        var trimmed = pathOrFolder.Trim().Trim('"');

        if (SafeCheck(fileExists, trimmed))
            return trimmed;

        if (SafeCheck(directoryExists, trimmed))
            return FindExternalAudioExe(new[] { trimmed }, fileExists, directoryExists);

        return null;
    }

    /// <summary>
    /// Every path worth checking, in probe order: each root itself, then each known SRS folder
    /// name below it, each combined with each known executable location. Roots that don't exist
    /// are skipped so a missing D: drive costs nothing.
    /// </summary>
    internal static IEnumerable<string> EnumerateCandidates(
        IEnumerable<string> searchRoots,
        Func<string, bool> directoryExists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !SafeCheck(directoryExists, root))
                continue;

            foreach (var installFolder in InstallFolders(root))
            {
                if (!SafeCheck(directoryExists, installFolder))
                    continue;

                foreach (var relative in ExeRelativePaths)
                {
                    var candidate = Combine(installFolder, relative);
                    if (seen.Add(candidate))
                        yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> InstallFolders(string root)
    {
        // The root may already BE the install folder (someone pointing at their SRS directory),
        // so probe it directly before looking for an install folder inside it.
        yield return root;

        foreach (var name in InstallFolderNames)
            yield return Path.Combine(root, name);
    }

    private static string Combine(string baseDir, string[] segments)
    {
        var path = baseDir;
        foreach (var segment in segments)
            path = Path.Combine(path, segment);
        return path;
    }

    /// <summary>
    /// The places an SRS install is likely to live: both Program Files variants, per-user
    /// install locations, and the root of every fixed drive (people do install it to D:\).
    /// </summary>
    internal static IEnumerable<string> DefaultSearchRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            string path;
            try { path = Environment.GetFolderPath(folder); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(path))
                continue;

            yield return path;

            if (folder == Environment.SpecialFolder.LocalApplicationData)
                yield return Path.Combine(path, "Programs");
        }

        string[] drives;
        try
        {
            drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            yield return drive;
            yield return Path.Combine(drive, "Program Files");
            yield return Path.Combine(drive, "Games");
        }
    }

    /// <summary>
    /// An existence check that treats "I'm not allowed to look" as "not there". Probing drive
    /// roots and other users' profiles otherwise throws on locked-down machines.
    /// </summary>
    private static bool SafeCheck(Func<string, bool> check, string path)
    {
        try { return check(path); }
        catch { return false; }
    }
}
