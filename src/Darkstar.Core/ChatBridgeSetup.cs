using System.Runtime.InteropServices;

namespace Darkstar;

/// <summary>Whether a Saved Games folder has the chat bridge hook, and whether it is this build's.</summary>
public enum HookState
{
    Missing,
    Current,

    /// <summary>A hook is there, but not the one this build ships - an older version, or hand-edited.</summary>
    Outdated,
}

/// <summary>One DCS Saved Games folder found on this PC, and what it looks like.</summary>
/// <param name="Path">The Saved Games folder of that DCS, e.g. <c>…\Saved Games\DCS.dcs_serverrelease</c>.</param>
/// <param name="Profile">The Windows profile it belongs to - a dedicated server often runs under its own account.</param>
/// <param name="DedicatedServer">The folder name says so: DCS.dcs_serverrelease, DCS.openbeta_server, ...</param>
/// <param name="HasServerSettings">There is a <c>Config\serverSettings.lua</c>: this DCS has hosted a server at some point.</param>
/// <param name="HasGrpcHook">DCS-gRPC's own hook is installed here - the strongest sign this is the server the bot talks to.</param>
/// <param name="LastUsedUtc">When <c>Logs\dcs.log</c> was last written, i.e. when this DCS last ran.</param>
/// <param name="LooksLikeDcs">False for a folder somebody picked by hand that has none of DCS's own files.</param>
public sealed record DcsFolder(
    string Path,
    string Name,
    string Profile,
    bool DedicatedServer,
    bool HasServerSettings,
    bool HasGrpcHook,
    HookState Hook,
    DateTime? LastUsedUtc,
    bool LooksLikeDcs)
{
    /// <summary>Where the hook goes in this folder.</summary>
    public string HookPath => ChatBridgeSetup.HookPathFor(Path);

    /// <summary>The folder the hook will write to, and <c>ChatBridgeFolder</c> has to name.</summary>
    public string BridgeFolder => ChatBridgeSetup.BridgeFolderFor(Path);
}

/// <summary>What a search found, and which profiles Windows would not let it look into.</summary>
public sealed record DcsFolderScan(IReadOnlyList<DcsFolder> Folders, IReadOnlyList<string> Unreadable);

/// <summary>
/// The config editor's setup for the chat bridge: find the DCS Saved Games folders on this PC,
/// say which of them look like the server, and put the hook into the one the operator picks.
///
/// Picking is left to the operator on purpose. A PC can have several DCS installations - a client,
/// an open beta, a dedicated server started with or without <c>-w</c> - and the bot has no way to
/// tell which of them is the server it is meant for. A wrong guess fails silently (the hook writes
/// into one folder, the bot reads another), so the editor shows what it found, with every hint it
/// has, and lets a person decide.
///
/// The hook itself is embedded in this assembly, so the editor can install it wherever it was
/// installed itself - including the bot-only setup, where there is no <c>dcs\</c> folder next to
/// it to copy from. A test keeps the embedded copy identical to the file in the repository.
/// </summary>
public static class ChatBridgeSetup
{
    /// <summary>The embedded hook's resource name, set in Darkstar.Core.csproj.</summary>
    public const string ResourceName = "Darkstar.ChatBridge.lua";

    private static readonly Lazy<byte[]> Script = new(() =>
    {
        using var stream = typeof(ChatBridgeSetup).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The hook is not embedded ({ResourceName}).");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    /// <summary>The hook, exactly as this build ships it.</summary>
    public static byte[] HookScript => Script.Value;

    /// <summary>Where the hook goes in a Saved Games folder: <c>Scripts\Hooks\Darkstar-ChatBridge.lua</c>.</summary>
    public static string HookPathFor(string savedGamesFolder) =>
        Path.Combine(savedGamesFolder, "Scripts", "Hooks", ChatBridge.HookFileName);

    /// <summary>
    /// The folder the hook writes to in that Saved Games folder - what <c>ChatBridgeFolder</c> has to
    /// be. The same rule <see cref="ChatBridge.ExpectedHookPath"/> checks the other way round.
    /// </summary>
    public static string BridgeFolderFor(string savedGamesFolder) =>
        Path.Combine(savedGamesFolder, "Darkstar", "ChatBridge");

    /// <summary>
    /// The Saved Games folder a hand-picked folder stands for. Somebody pointing at the
    /// <c>Scripts\Hooks</c> folder, or at <c>Darkstar\ChatBridge</c>, means the Saved Games folder
    /// above it - the same neighbours <see cref="ChatBridge.ResolveFolder"/> accepts.
    /// </summary>
    public static string SavedGamesFolderOf(string picked)
    {
        var folder = (picked ?? "").Trim().TrimEnd('\\', '/');
        if (folder.Length == 0) return "";

        var resolved = ChatBridge.ResolveFolder(folder);
        if (ChatBridge.ExpectedHookPath(resolved) is { } hook)
            return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(hook)!)!)!;

        return folder;
    }

    // -----------------------------------------------------------------------------------------
    // Finding the folders
    // -----------------------------------------------------------------------------------------

    private static readonly string[] NotAProfile = { "Public", "Default", "Default User", "All Users" };

    /// <summary>Searches every Saved Games folder on this PC that Windows lets the editor read.</summary>
    public static DcsFolderScan Scan()
    {
        var unreadable = new List<string>();
        var roots = SavedGamesRoots(OwnSavedGamesFolder(), ProfilesFolder(), unreadable);
        var found = Scan(roots);
        return new DcsFolderScan(found.Folders, unreadable.Concat(found.Unreadable).ToList());
    }

    /// <summary>
    /// The Saved Games folders to search: this user's own (wherever it was moved to) and the one in
    /// every other profile under <paramref name="profilesFolder"/>. A dedicated server often runs
    /// under an account of its own, and that is exactly the folder the operator is looking for.
    /// </summary>
    internal static IReadOnlyList<string> SavedGamesRoots(string? ownSavedGames, string? profilesFolder, List<string> unreadable)
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = path.TrimEnd('\\', '/');
            if (!Directory.Exists(full)) return;
            if (roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(full);
        }

        Add(ownSavedGames);

        if (!string.IsNullOrWhiteSpace(profilesFolder) && Directory.Exists(profilesFolder))
        {
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(profilesFolder).ToList(); }
            catch (Exception) { profiles = Array.Empty<string>(); unreadable.Add(profilesFolder); }

            foreach (var profile in profiles)
            {
                // Not people: Windows' templates and the old names kept as links for compatibility,
                // which refuse every look inside and would only fill the "could not read" list.
                if (NotAProfile.Contains(Path.GetFileName(profile), StringComparer.OrdinalIgnoreCase)) continue;

                try
                {
                    var savedGames = Path.Combine(profile, "Saved Games");
                    if (Directory.Exists(savedGames))
                    {
                        Add(savedGames);
                    }
                    else
                    {
                        // Directory.Exists says "no" both when there is nothing and when Windows
                        // does not let us look. Looking into the profile tells the two apart, so a
                        // server account the editor cannot read is reported instead of skipped.
                        _ = Directory.EnumerateFileSystemEntries(profile).Any();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    unreadable.Add(profile);
                }
                catch (IOException)
                {
                    unreadable.Add(profile);
                }
            }
        }

        return roots;
    }

    /// <summary>Searches the given Saved Games folders for DCS folders, most likely server first.</summary>
    public static DcsFolderScan Scan(IEnumerable<string> savedGamesRoots)
    {
        var folders = new List<DcsFolder>();
        var unreadable = new List<string>();

        foreach (var root in savedGamesRoots)
        {
            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadable.Add(root);
                continue;
            }

            var profile = Path.GetFileName(Path.GetDirectoryName(root) ?? "") ?? "";

            foreach (var child in children)
            {
                try
                {
                    if (IsDcsFolder(child)) folders.Add(Inspect(child, profile));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    unreadable.Add(child);
                }
            }
        }

        return new DcsFolderScan(Order(folders), unreadable);
    }

    /// <summary>
    /// The most likely server first: a folder named as a dedicated server's, then one with DCS-gRPC's
    /// hook, then one that has hosted a server, then whichever ran most recently.
    /// </summary>
    internal static List<DcsFolder> Order(IEnumerable<DcsFolder> folders) => folders
        .OrderByDescending(f => f.DedicatedServer)
        .ThenByDescending(f => f.HasGrpcHook)
        .ThenByDescending(f => f.HasServerSettings)
        .ThenByDescending(f => f.LastUsedUtc ?? DateTime.MinValue)
        .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Whether a folder is a DCS Saved Games folder: DCS writes <c>Config\options.lua</c> and
    /// <c>Logs\dcs.log</c> on its first start, and a server <c>Config\serverSettings.lua</c>. By
    /// content rather than by name, because a server started with <c>-w Anything</c> uses
    /// <c>Saved Games\Anything</c>.
    /// </summary>
    internal static bool IsDcsFolder(string folder) =>
        File.Exists(Path.Combine(folder, "Config", "options.lua")) ||
        File.Exists(Path.Combine(folder, "Config", "serverSettings.lua")) ||
        File.Exists(Path.Combine(folder, "Logs", "dcs.log")) ||
        (Path.GetFileName(folder).StartsWith("DCS", StringComparison.OrdinalIgnoreCase) &&
         Directory.Exists(Path.Combine(folder, "Config")));

    /// <summary>Looks at one Saved Games folder - found by the search or picked by hand.</summary>
    public static DcsFolder Inspect(string folder, string profile = "")
    {
        var path = folder.TrimEnd('\\', '/');
        var name = Path.GetFileName(path);
        var log = Path.Combine(path, "Logs", "dcs.log");

        return new DcsFolder(
            path,
            name,
            profile,
            DedicatedServer: name.Contains("server", StringComparison.OrdinalIgnoreCase),
            HasServerSettings: File.Exists(Path.Combine(path, "Config", "serverSettings.lua")),
            HasGrpcHook: File.Exists(Path.Combine(path, "Scripts", "Hooks", "DCS-gRPC.lua")),
            Hook: HookStateOf(path),
            LastUsedUtc: File.Exists(log) ? File.GetLastWriteTimeUtc(log) : null,
            LooksLikeDcs: IsDcsFolder(path));
    }

    /// <summary>Whether the hook is there, and whether it is the one this build ships.</summary>
    public static HookState HookStateOf(string savedGamesFolder)
    {
        var hook = HookPathFor(savedGamesFolder);
        if (!File.Exists(hook)) return HookState.Missing;

        try
        {
            return SameScript(File.ReadAllBytes(hook), HookScript) ? HookState.Current : HookState.Outdated;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return HookState.Outdated;
        }
    }

    /// <summary>
    /// Same script, whatever line endings a copy, a git checkout or an editor gave it. DCS does not
    /// care, and an "outdated" warning about a CRLF would be noise.
    /// </summary>
    internal static bool SameScript(byte[] a, byte[] b)
    {
        static string Normal(byte[] bytes) =>
            System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").TrimEnd();

        return Normal(a) == Normal(b);
    }

    // -----------------------------------------------------------------------------------------
    // Installing and removing
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Puts this build's hook into <c>Scripts\Hooks</c> of a Saved Games folder, replacing an older
    /// one, and creates the folder it writes to. Returns the hook's path. DCS loads hooks at start,
    /// so the server needs a restart afterwards.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder belongs to another Windows account.</exception>
    public static string Install(string savedGamesFolder)
    {
        var hook = HookPathFor(savedGamesFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);

        // Through a temporary file: a DCS starting at that very moment reads the old hook or the
        // new one, never half of one.
        var temp = hook + ".tmp";
        File.WriteAllBytes(temp, HookScript);
        File.Move(temp, hook, overwrite: true);

        Directory.CreateDirectory(BridgeFolderFor(savedGamesFolder));
        return hook;
    }

    /// <summary>
    /// Takes the hook out of a Saved Games folder. Only the hook - the bridge folder may still hold
    /// requests, and the bot's heartbeat file is harmless. False when there was no hook.
    /// </summary>
    public static bool Remove(string savedGamesFolder)
    {
        var hook = HookPathFor(savedGamesFolder);
        if (!File.Exists(hook)) return false;

        File.Delete(hook);
        return true;
    }

    // -----------------------------------------------------------------------------------------
    // Where Windows keeps things
    // -----------------------------------------------------------------------------------------

    private static readonly Guid SavedGamesFolderId = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags, IntPtr token, out IntPtr path);

    /// <summary>
    /// This user's Saved Games folder, asked from Windows rather than assumed: it can be moved to
    /// another drive. Falls back to the usual place when Windows cannot answer.
    /// </summary>
    internal static string? OwnSavedGamesFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (SHGetKnownFolderPath(SavedGamesFolderId, 0, IntPtr.Zero, out var pointer) == 0)
                {
                    try { return Marshal.PtrToStringUni(pointer); }
                    finally { Marshal.FreeCoTaskMem(pointer); }
                }
            }
            catch (Exception) { /* fall back below */ }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Saved Games");
    }

    /// <summary>The folder all profiles live in - <c>C:\Users</c> - taken from this user's own.</summary>
    internal static string? ProfilesFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile);
    }
}
