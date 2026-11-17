using System.Text.RegularExpressions;
using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The config editor's search (<see cref="EditorSearch"/>). Its index is a table generated from the
/// panels, so the most important checks hold the table against the panels: what it names must be
/// on screen where it says, and everything a panel edits must be findable by its config.json name.
/// A panel that gains a field without an index entry fails here, not in somebody's hands.
/// </summary>
internal static class EditorSearchTests
{
    /// <summary>The panel file behind each page key the index uses.</summary>
    private static readonly Dictionary<string, string> PanelFiles = new()
    {
        ["connection"] = "ConnectionPanel.razor",
        ["service"] = "ServicePanel.razor",
        ["profiles"] = "ProfilesPanel.razor",
        ["radios"] = "RadiosPanel.razor",
        ["speech"] = "SpeechPanel.razor",
        ["phrases"] = "PhrasesPanel.razor",
        ["vocabulary"] = "VocabularyPanel.razor",
        ["dcsgrpc"] = "DcsGrpcPanel.razor",
        ["discord"] = "DiscordPanel.razor",
        ["logging"] = "LoggingPanel.razor",
        ["checklist"] = "ChecklistPanel.razor",
    };

    private static string Panel(string page) =>
        System.Net.WebUtility.HtmlDecode(ReadSource("src/Darkstar.Gui/Pages/" + PanelFiles[page]));

    public static void Run()
    {
        Section("Every entry points at something that is on that page");

        var unknownPages = EditorSearch.Targets.Select(t => t.Page).Distinct().Where(p => !PanelFiles.ContainsKey(p)).ToList();
        Check("every page in the index is one the editor has", unknownPages.Count == 0, string.Join(", ", unknownPages));
        if (unknownPages.Count > 0) return;

        var notOnPage = EditorSearch.Targets
            .Where(t => !Panel(t.Page).Contains(t.Label, StringComparison.Ordinal))
            .Select(t => $"{t.Page}: \"{t.Label}\"")
            .ToList();
        Check($"every label is on its page, as the page shows it ({EditorSearch.Targets.Count} entries)",
            notOnPage.Count == 0, string.Join("; ", notOnPage.Take(5)));

        var cardsMissing = EditorSearch.Targets
            .Where(t => t.Card != null && !Panel(t.Page).Contains(t.Card, StringComparison.Ordinal))
            .Select(t => $"{t.Page}: \"{t.Card}\"")
            .Distinct()
            .ToList();
        Check("and so is every card it names", cardsMissing.Count == 0, string.Join("; ", cardsMissing.Take(5)));

        var badSections = EditorSearch.Targets
            .Where(t => t.Page == "dcsgrpc" && t.Section is not ("server" or "replies" or "explorer"))
            .Select(t => t.Label)
            .ToList();
        Check("every CH8 entry says which part of CH8", badSections.Count == 0, string.Join("; ", badSections));

        var unnamed = EditorSearch.Targets.Where(t => EditorSearch.PageName(t.Page, t.Section) == t.Page).Select(t => t.Page).Distinct().ToList();
        Check("every page has the name the sidebar gives it", unnamed.Count == 0, string.Join(", ", unnamed));

        Section("Everything a panel edits can be found by its config.json name");

        // Taken from the panels themselves: a setting a panel binds, reads or writes is something a
        // person may look for, and searching for its name has to land on that panel.
        var unfindable = new List<string>();
        var checkedSettings = 0;

        foreach (var (page, file) in PanelFiles)
        {
            if (page == "checklist") continue;

            var source = ReadSource("src/Darkstar.Gui/Pages/" + file);
            var settings = Regex.Matches(source, @"Store\.Config\.(\w+)").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(source, @"\bradio\.(\w+)\b").Select(m => m.Groups[1].Value))
                .Where(name => typeof(AppConfig).GetProperty(name) != null || typeof(RadioConfig).GetProperty(name) != null)
                .Distinct();

            foreach (var setting in settings)
            {
                checkedSettings++;
                if (!EditorSearch.Find(setting).Any(t => t.Page == page))
                    unfindable.Add($"{setting} ({file})");
            }
        }

        Check($"all {checkedSettings} settings the panels use are findable on their panel",
            checkedSettings > 100 && unfindable.Count == 0,
            unfindable.Count == 0 ? $"{checkedSettings} checked" : "not found: " + string.Join(", ", unfindable.Take(8)));

        Section("What a search returns");

        static string First(string query) => EditorSearch.Find(query).FirstOrDefault()?.Title ?? "(nothing)";

        Eq("a setting's name finds its field first", First("ChatBridgeFolder"), "Chat bridge folder");
        Eq("in any case", First("chatbridgefolder"), "Chat bridge folder");
        Check("a word from the label finds it", EditorSearch.Find("bridge folder").Any(t => t.Title == "Chat bridge folder"));
        Check("a card is found by its title",
            First("threat circle").StartsWith("Threat circle", StringComparison.Ordinal), First("threat circle"));
        Check("every word has to match - another word narrows the list",
            EditorSearch.Find("reply").Count >= EditorSearch.Find("reply threat").Count &&
            EditorSearch.Find("reply threat").All(t => (t.Title + t.Where + t.Keywords).Contains("hreat", StringComparison.OrdinalIgnoreCase)));
        Check("German words find the English labels: Stimme",
            EditorSearch.Find("Stimme").Any(t => t.Title.Contains("voice", StringComparison.OrdinalIgnoreCase)));
        Check("Funkrad", EditorSearch.Find("Funkrad").Any(t => t.Page == "dcsgrpc" && t.Section == "replies"));
        Check("Sicherung", EditorSearch.Find("Sicherung").Any(t => t.Page == "profiles"));
        Check("the checklist is findable too", EditorSearch.Find("checklist").Any(t => t.Page == "checklist"));
        Check("a result says where it is",
            EditorSearch.Find("ChatBridgeFolder")[0].Where.StartsWith("CH8 Replies", StringComparison.Ordinal),
            EditorSearch.Find("ChatBridgeFolder")[0].Where);
        Check("nothing typed is nothing found", EditorSearch.Find("").Count == 0 && EditorSearch.Find("   ").Count == 0);
        Check("nonsense is nothing found", EditorSearch.Find("xyzzyplugh").Count == 0);
        Check("never more than the sidebar shows", EditorSearch.Find("reply").Count <= EditorSearch.MaxResults);

        Section("The editor uses it");

        var main = ReadSource("src/Darkstar.Gui/Pages/Main.razor");
        Check("the search box is in the sidebar", main.Contains("id=\"editor-search\"", StringComparison.Ordinal));
        Check("its results come from the index", main.Contains("EditorSearch.Find(_query)", StringComparison.Ordinal));
        // InvokeVoidAsync is an extension method in Microsoft.JSInterop: naming IJSRuntime by its
        // full name compiles the injection and then fails on the call.
        Check("the JS interop namespace is imported, for its extension methods",
            main.Contains("@using Microsoft.JSInterop", StringComparison.Ordinal));
        Check("opening a result scrolls to it on its page",
            main.Contains("JS.InvokeVoidAsync(\"darkstarEditor.reveal\", target.Label, target.Card)", StringComparison.Ordinal));

        var script = ReadSource("src/Darkstar.Gui/wwwroot/js/editor.js");
        Check("the script that scrolls is there", script.Contains("window.darkstarEditor", StringComparison.Ordinal) &&
                                                  script.Contains("reveal:", StringComparison.Ordinal));
        Check("Ctrl+K reaches the search box from anywhere",
            script.Contains("e.ctrlKey", StringComparison.Ordinal) && script.Contains("getElementById(\"editor-search\")", StringComparison.Ordinal));
        Check("and the page loads it",
            ReadSource("src/Darkstar.Gui/wwwroot/index.html").Contains("<script src=\"js/editor.js\"></script>", StringComparison.Ordinal));
    }
}
