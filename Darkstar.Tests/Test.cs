namespace Darkstar.Tests;

/// <summary>
/// The whole test harness: a counter, two assertions and a way to find the repository.
///
/// WHY NOT xUnit OR NUNIT: these tests are about a radio bot, and most of them assert a spoken
/// sentence, an arithmetic result, or that a source file still says what a comment claims it says.
/// None of that needs a test framework, a runner plugin or an attribute vocabulary - and a
/// dependency-free test project is one that still builds in five years, runs with a plain
/// "dotnet run", and can be read start to finish by somebody who has never seen this repository.
///
/// The output is deliberately a readable transcript rather than a dot per test. When something
/// fails at three in the morning on somebody else's machine, the line above the failure is usually
/// what explains it.
/// </summary>
internal static class Test
{
    public static int Passed;
    public static int Failed;

    /// <summary>One assertion. <paramref name="detail"/> is printed either way - on a pass it
    /// documents what the value actually was, which is often more useful than the name.</summary>
    public static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            Passed++;
            Console.WriteLine($"  [ok]   {name}{(detail == "" ? "" : "   → " + detail)}");
        }
        else
        {
            Failed++;
            Console.WriteLine($"  [FAIL] {name}   {detail}");
        }
    }

    /// <summary>String equality, which reports both values on a mismatch rather than just "false".</summary>
    public static void Eq(string name, string actual, string expected) =>
        Check(name, actual == expected,
            actual == expected ? actual : $"got \"{actual}\", expected \"{expected}\"");

    /// <summary>A blank line and a heading, so the transcript can be read in sections.</summary>
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    private static string? _root;

    /// <summary>
    /// The repository root, with a trailing separator.
    ///
    /// Found by walking up from the test assembly until <c>Darkstar.sln</c> appears, rather than by
    /// counting "..\..\..\" levels - that count changes with the target framework and the build
    /// configuration, and the resulting failure looks like a broken test rather than a moved file.
    ///
    /// Several tests read source files as text to assert that the wiring still matches what the
    /// comments promise: that a radio check is answered before the tactical classifier, that no
    /// user input can reach the runway Lua, that the documentation covers every setting. Those
    /// properties live in the arrangement of the code, not in its behaviour, so there is nothing
    /// else to call.
    /// </summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;

            // The assembly's own folder first, which is the normal case, then the working directory -
            // that second one covers a runner that puts its output somewhere else entirely, and
            // costs nothing when the first already worked.
            var found = SearchUpwards(AppContext.BaseDirectory) ?? SearchUpwards(Environment.CurrentDirectory);

            if (found == null)
                throw new InvalidOperationException(
                    $"Could not find Darkstar.sln above \"{AppContext.BaseDirectory}\" " +
                    $"or \"{Environment.CurrentDirectory}\" - run the tests from inside the repository.");

            return _root = found + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>Walks up from a folder looking for the solution file. Null if it isn't above it.</summary>
    private static string? SearchUpwards(string startingFolder)
    {
        var directory = new DirectoryInfo(startingFolder);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Darkstar.sln")))
            directory = directory.Parent;

        return directory?.FullName;
    }

    /// <summary>Reads a file from the repository, by a path relative to its root.</summary>
    public static string ReadSource(string relativePath) =>
        File.ReadAllText(Root + relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Any line in this project's own sources that contains a rooted path literal - the mistake that
    /// makes a suite pass on one machine and throw on every other one. Reported as
    /// "file:line" strings; an empty list is the healthy case.
    /// </summary>
    /// <remarks>
    /// Matches a quoted string starting with a Unix system root or a Windows drive letter, which
    /// covers what actually gets pasted in by accident. Paths built from <see cref="Root"/> and
    /// relative paths are untouched, and so are absolute paths the tests construct at runtime -
    /// <c>Path.GetTempPath()</c> is how a test writes scratch files, and that is fine.
    /// </remarks>
    public static List<string> FindAbsolutePaths()
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            "\"(?:/(?:home|tmp|Users|var|mnt)/|[A-Za-z]:\\\\\\\\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        var found = new List<string>();

        foreach (var file in Directory.GetFiles(Root + "Darkstar.Tests", "*.cs").OrderBy(f => f))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // This method's own pattern would otherwise report itself.
                if (lines[i].Contains("[A-Za-z]:", StringComparison.Ordinal)) continue;

                if (pattern.IsMatch(lines[i]))
                    found.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        return found;
    }
}
