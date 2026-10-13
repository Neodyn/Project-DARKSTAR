using System.Reflection;

namespace Darkstar;

/// <summary>
/// Which build is this? One place that answers it, for the bot's log, the config editor's title
/// bar and the <c>--version</c> switch.
///
/// WHY THIS EXISTS AT ALL: nothing in the installed files used to say which build they were. Every
/// assembly reported 1.0.0.0 because no version was ever set, so a bot running an old binary looked
/// exactly like a bot running a new one - and "the installer seems to contain an older version" was
/// impossible to confirm or rule out from the outside. It took a reproduction to find the cause
/// (see <c>build-installer.ps1</c>), which is a lot of work for a question a single log line can
/// answer.
///
/// WHERE THE VALUES COME FROM: the build stamps them in.
///
///   Version                 1.2.0.0            what -Version was given to build-installer.ps1
///   InformationalVersion    1.2+build.20261008143300   the same, plus the UTC build time
///
/// The build-time suffix is not decoration. It makes every build's version string unique, which is
/// what forces MSBuild to recompile instead of silently reusing output that is newer than a source
/// file whose timestamp happens to lie in the past - the exact failure this was built to end.
/// </summary>
public static class VersionInfo
{
    /// <summary>What an unversioned build reports, i.e. one not made by the build script.</summary>
    public const string DevelopmentVersion = "0.0.0-dev";

    private static readonly Lazy<(string Version, DateTime? BuiltUtc)> Stamp = new(Read);

    /// <summary>"1.2" - the release number alone, without the build stamp.</summary>
    public static string Version => Stamp.Value.Version;

    /// <summary>When this assembly was built, or null when the build left no stamp.</summary>
    public static DateTime? BuiltUtc => Stamp.Value.BuiltUtc;

    /// <summary>Whether this is a build from the installer script rather than someone's own.</summary>
    public static bool IsRelease => Version != DevelopmentVersion;

    /// <summary>
    /// "1.2 (built 2026-10-08 14:33 UTC)", or "0.0.0-dev" for a build from the IDE.
    /// Short enough for a title bar and for the first line of a log.
    /// </summary>
    public static string Display =>
        BuiltUtc is { } built
            ? $"{Version} (built {built:yyyy-MM-dd HH:mm} UTC)"
            : Version;

    /// <summary>
    /// Reads the stamp out of the running assembly.
    /// </summary>
    /// <remarks>
    /// Deliberately reads the assembly this type lives in rather than the entry assembly: the bot
    /// and the config editor are different executables sharing this one, and a mismatch between
    /// them is itself worth seeing. Anything unreadable degrades to the development version rather
    /// than throwing - a version string is never worth taking the bot down for.
    /// </remarks>
    private static (string, DateTime?) Read()
    {
        try
        {
            var assembly = typeof(VersionInfo).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
                return (assembly.GetName().Version?.ToString() ?? DevelopmentVersion, null);

            return Parse(informational);
        }
        catch
        {
            return (DevelopmentVersion, null);
        }
    }

    /// <summary>
    /// Splits "1.2+build.20261008143300" into its two halves. Internal so the tests can feed it
    /// the shapes a build can produce without having to produce them.
    /// </summary>
    internal static (string Version, DateTime? BuiltUtc) Parse(string informationalVersion)
    {
        var text = informationalVersion.Trim();

        // SourceLink and friends append their own "+<commit>" metadata, so take the first marker
        // and keep what is in front of it as the version either way.
        var plus = text.IndexOf('+');
        if (plus < 0) return (text, null);

        var version = text[..plus];
        var metadata = text[(plus + 1)..];

        const string marker = "build.";
        if (!metadata.StartsWith(marker, StringComparison.Ordinal))
            return (version, null);   // somebody else's metadata, not ours

        var stamp = metadata[marker.Length..];

        return DateTime.TryParseExact(stamp, "yyyyMMddHHmmss",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal |
                   System.Globalization.DateTimeStyles.AdjustToUniversal, out var built)
            ? (version, built)
            : (version, null);
    }
}
