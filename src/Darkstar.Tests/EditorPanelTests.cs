using System.Text.RegularExpressions;
using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The editor's layout: the order of the channels in the sidebar, and the path fields that can be
/// browsed to rather than typed.
///
/// Both are read off the source. There is no way to click this editor from a test - it is a WPF
/// window hosting a WebView - so what can be checked is that the markup says what it is supposed
/// to say. That is enough for the two mistakes that actually happen: a panel quietly changing
/// place in the sidebar, and a new path field shipping without a Browse button beside the five
/// that have one.
/// </summary>
internal static class EditorPanelTests
{
    /// <summary>
    /// The sidebar, top to bottom: group titles and the entries under them.
    ///
    /// Written out rather than derived, because the order is a decision and this is where it is
    /// recorded. Changing the sidebar fails this check, which is the intended cost - the order was
    /// wrong three times running, and an accident should not be indistinguishable from a decision.
    ///
    /// The rule, in full: a group comes before another when its lowest channel number is lower,
    /// and inside a group the numbers ascend. So SERVER (1), RADIO &amp; SPEECH (4),
    /// MISSION DATA (8), MONITORING (9) - and the sidebar counts straight up, 1 to 10.
    ///
    /// Reaching that took reassigning the numbers once, which this project had treated as
    /// forbidden because the documentation names them in about three hundred places. Two earlier
    /// attempts kept the numbers and sorted the groups by how a setup is actually done; both
    /// produced a sidebar that jumped (1, 9, 10, 2, 3, 4, 5, 8, 7, 6 at worst), and both were
    /// rejected - correctly, because the numbers are the index people navigate by and a channel
    /// selector counts upwards. The numbers are fixed again from here; what moved, moved once.
    ///
    /// CH8 stays one group of three parts (connect, configure, inspect) with no numbers of its
    /// own, because all three are CH8. Its position comes from its title.
    /// </summary>
    private static readonly string[] ExpectedSidebar =
    {
        "SERVER",
        "CH1 CONNECTION",
        "CH2 SERVICE",
        "CH3 PROFILES",

        "RADIO & SPEECH",
        "CH4 RADIOS",
        "CH5 SPEECH",
        "CH6 PHRASES",
        "CH7 VOCABULARY",

        "MISSION DATA — CH8",
        "SERVER & TEST",
        "REPLIES",
        "MISSION EXPLORER",

        "MONITORING",
        "CH9 DISCORD",
        "CH10 LOGGING",
    };

    /// <summary>
    /// Every path setting that is browsable, and whether it names a folder or a file. The value is
    /// the picker method the handler has to call - pointing a folder field at a file picker is the
    /// mistake this pairing exists to catch, and it would look perfectly fine in a screenshot.
    /// </summary>
    /// <remarks>
    /// <c>Applies</c> is what the handler must be seen to do with the chosen path. Checking only
    /// that the word "picked" appears was no check at all - replacing the assignment with
    /// <c>_ = picked;</c> left a button that visibly does nothing and fired no test.
    /// </remarks>
    private static readonly (string Binding, string Picker, string Applies)[] ExpectedPathFields =
    {
        // CH1: the SRS tool every reply is transmitted through.
        ("Store.Config.ExternalAudioExePath", "PickFile", "Store.Config.ExternalAudioExePath = picked"),

        // CH5: a Whisper model is one .bin file; a Vosk model is a folder of four subfolders.
        ("Store.Config.WhisperModelPath", "PickFile", "Store.Config.WhisperModelPath = picked"),
        ("Store.Config.VoskModelPath", "PickFolder", "Store.Config.VoskModelPath = picked"),

        // CH2: which executable to register as the service.
        ("_exePath", "PickFile", "_exePath = picked"),

        // CH3: filled in only - loading it is a separate decision, made by its own button.
        ("_folder", "PickFolder", "_folder = picked"),

        // The "not loaded" screen, where there is nothing on screen to lose, so it loads at once.
        ("Store.ConfigFolder", "PickFolder", "Store.UseFolder(picked)"),
    };

    private static string PagesFolder => Path.Combine(Root, "src", "Darkstar.Gui", "Pages");

    public static void Run()
    {
        Section("The sidebar is in the order it was decided to be in");

        var main = ReadSource("src/Darkstar.Gui/Pages/Main.razor");

        // Groups and entries in one pass, so the result is document order rather than two lists
        // that have to be stitched back together.
        var sidebar = Regex
            .Matches(main, @"new NavGroup\(""(?<group>[^""]*)""|new NavEntry\(""[^""]*"",\s*""(?<ch>[^""]*)"",\s*""(?<label>[^""]*)""")
            .Select(m => m.Groups["group"].Success
                ? m.Groups["group"].Value
                : (m.Groups["ch"].Value.Length > 0
                    ? $"{m.Groups["ch"].Value} {m.Groups["label"].Value}"
                    : m.Groups["label"].Value))
            .ToList();

        Check("the sidebar was parsed", sidebar.Count == ExpectedSidebar.Length,
            $"{sidebar.Count} entries: {string.Join(" | ", sidebar)}");

        var firstDifference = sidebar
            .Zip(ExpectedSidebar, (actual, expected) => (actual, expected))
            .FirstOrDefault(p => p.actual != p.expected);

        Check("and it is in the recorded order", firstDifference.actual == null,
            firstDifference.actual == null
                ? ""
                : $"expected \"{firstDifference.expected}\", found \"{firstDifference.actual}\"");

        Section("And the order follows the rule, not just the list above");

        // The list pins what the sidebar is; this pins WHY. Without it the list is a photograph -
        // somebody adding CH11 updates it to match whatever they built and the check still passes,
        // which is how the order drifted the first two times.
        var groups = new List<(string Title, List<int> Numbers)>();

        foreach (Match match in Regex.Matches(main,
                     @"new NavGroup\(""(?<group>[^""]*)""|new NavEntry\(""[^""]*"",\s*""CH(?<n>\d+)"""))
        {
            if (match.Groups["group"].Success)
                groups.Add((match.Groups["group"].Value, new List<int>()));
            else if (groups.Count > 0)
                groups[^1].Numbers.Add(int.Parse(match.Groups["n"].Value));
        }

        Check("the groups were found", groups.Count == 4, string.Join(", ", groups.Select(g => g.Title)));

        foreach (var (title, numbers) in groups)
        {
            // CH8's group carries no numbers of its own - all three entries are CH8 - so there is
            // nothing to be out of order there.
            if (numbers.Count < 2) continue;

            var ascending = numbers.Zip(numbers.Skip(1), (a, b) => a < b).All(ok => ok);
            Check($"{title} ascends: {string.Join(", ", numbers)}", ascending);
        }

        // A group's position is decided by its lowest number, which is what makes the rule total:
        // every new panel has exactly one place to go. A group whose entries carry no number of
        // their own takes it from its own title - "MISSION DATA — CH8" - rather than from a
        // constant here, so renumbering that group could not silently pass.
        var minima = groups
            .Select(g => g.Numbers.Count > 0
                ? g.Numbers.Min()
                : int.Parse(Regex.Match(g.Title, @"CH(\d+)").Groups[1].Value))
            .ToList();
        Check($"the groups themselves ascend by their lowest number: {string.Join(", ", minima)}",
            minima.Zip(minima.Skip(1), (a, b) => a < b).All(ok => ok));

        Section("Every path field can be browsed to");

        // The five path settings are the ones nobody can type from memory - an SRS install folder,
        // a model folder several levels down. A field without a Browse button is the one that gets
        // a wrong path typed into it, which is how most of CH1's and CH5's support questions start.
        var browsable = new List<string>();

        foreach (var file in Directory.GetFiles(PagesFolder, "*.razor").OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);

            foreach (Match row in Regex.Matches(text, @"<div class=""path-row"">(?<body>.*?)</div>",
                         RegexOptions.Singleline))
            {
                var body = row.Groups["body"].Value;

                var bound = Regex.Match(body, @"@bind=""(?<target>[^""]+)""");
                Check($"{Path.GetFileName(file)}: a path row binds something", bound.Success, body.Trim());
                if (!bound.Success) continue;

                var target = bound.Groups["target"].Value;
                browsable.Add(target);

                Check($"{target} has a Browse button",
                    body.Contains("class=\"browse\"", StringComparison.Ordinal), body.Trim());

                // Which picker it calls, found through the handler the button names.
                var handler = Regex.Match(body, @"class=""browse""[^>]*@onclick=""(?<method>[A-Za-z0-9_]+)""");
                Check($"{target}'s button names a handler", handler.Success, body.Trim());
                if (!handler.Success) continue;

                var wanted = ExpectedPathFields.FirstOrDefault(p => p.Binding == target);
                if (wanted.Picker == null) continue;
                var expected = wanted.Picker;

                // The handler's body: from its signature to the closing brace of the method.
                var method = Regex.Match(text,
                    @"private void " + handler.Groups["method"].Value + @"\(\)\s*\{(?<body>.*?)\n    \}",
                    RegexOptions.Singleline);

                Check($"{handler.Groups["method"].Value} is declared", method.Success);
                if (!method.Success) continue;

                Check($"{target} is asked for with {expected}",
                    method.Groups["body"].Value.Contains("Picker." + expected, StringComparison.Ordinal),
                    method.Groups["body"].Value.Trim());

                // A picked path that is dropped on the floor would leave a button that visibly
                // does nothing, which is worse than no button - so what the handler does with it
                // is named in the table above rather than merely "mentions it somewhere".
                Check($"{target} is applied with {wanted.Applies}",
                    method.Groups["body"].Value.Contains(wanted.Applies, StringComparison.Ordinal),
                    method.Groups["body"].Value.Trim());
            }
        }

        var missing = ExpectedPathFields
            .Where(p => !browsable.Contains(p.Binding))
            .Select(p => p.Binding)
            .ToList();

        Check($"all {ExpectedPathFields.Length} path settings are in a path row", missing.Count == 0,
            string.Join(", ", missing));

        // A new path field should not be able to ship without a browser, and the list above is
        // what makes that decision visible rather than implicit.
        var unlisted = browsable
            .Where(b => ExpectedPathFields.All(p => p.Binding != b))
            .ToList();

        Check("and no path row is browsing something this list does not know about",
            unlisted.Count == 0, string.Join(", ", unlisted));

        Section("The panels stay compilable without WPF");

        // The dialogs are WPF types and the panels are compiled against plain net8.0 by the
        // offline harness, which is where a Razor change gets checked at all. A panel that reached
        // for a dialog directly would build on Windows and nowhere else, and the failure would
        // look like a broken harness rather than a misplaced reference.
        foreach (var file in Directory.GetFiles(PagesFolder, "*.razor"))
        {
            var text = File.ReadAllText(file);

            Check($"{Path.GetFileName(file)} does not open a dialog itself",
                !text.Contains("OpenFolderDialog", StringComparison.Ordinal)
                && !text.Contains("OpenFileDialog", StringComparison.Ordinal));
        }

        Check("the picker is injected as an interface",
            ReadSource("src/Darkstar.Gui/Pages/SpeechPanel.razor")
                .Contains("@inject IPathPicker Picker", StringComparison.Ordinal));

        Check("and registered once for the whole editor",
            ReadSource("src/Darkstar.Gui/App.xaml.cs")
                .Contains("AddSingleton<IPathPicker, WindowsPathPicker>", StringComparison.Ordinal));

        // The implementation is the one file in the editor the harness cannot compile, so the two
        // things it must not do are checked as text: throw out of a Blazor handler, and assume it
        // is already on the UI thread.
        // Comments stripped: the file explains at length that nothing in it may throw, and a check
        // that reads the explanation as a violation would make documenting the rule impossible.
        var picker = string.Join("\n", ReadSource("src/Darkstar.Gui/WindowsPathPicker.cs")
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Check("the implementation marshals to the UI thread",
            picker.Contains("Dispatcher.Invoke", StringComparison.Ordinal));
        // "has a catch that returns null somewhere" passed even with the dialog's own catch
        // changed to rethrow, because the two small helpers below it still matched. Nothing in
        // this class may rethrow, so that is what is checked.
        Check("and swallows its own failures rather than killing the render loop",
            picker.Contains("catch (Exception", StringComparison.Ordinal)
            && !picker.Contains("throw", StringComparison.Ordinal),
            picker.Contains("throw", StringComparison.Ordinal) ? "it rethrows" : "");

        Section("The browse button is styled");

        // Grepping for the class name matched the later ".path-row input" rules even with the
        // layout rule itself renamed away, which left the button sitting under the input instead
        // of beside it. The layout is the point, so the layout is what is checked.
        var css = ReadSource("src/Darkstar.Gui/wwwroot/css/app.css");

        var rowRule = System.Text.RegularExpressions.Regex.Match(css,
            @"\.path-row\s*\{(?<body>[^}]*)\}");

        Check("the path row has a rule of its own", rowRule.Success);
        Check("and it puts the button beside the input rather than under it",
            rowRule.Success && rowRule.Groups["body"].Value.Contains("flex", StringComparison.Ordinal),
            rowRule.Success ? rowRule.Groups["body"].Value.Trim() : "");

        Check("the button is styled", System.Text.RegularExpressions.Regex.IsMatch(css,
            @"\.path-row \.browse\s*\{"));
    }
}
