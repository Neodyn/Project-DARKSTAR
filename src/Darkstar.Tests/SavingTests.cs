using System.Reflection;
using System.Text.Json;
using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// Whether pressing Save really writes what was set.
///
/// "I changed it, saved, and the bot behaves as before" has three possible causes, and only one of
/// them is the bot: the setting never reached the config object, the config object never reached
/// the file, or the file was not the one the bot reads. The third is <see cref="StartupPaths"/>'s
/// subject. This covers the second, and it covers it by reflection rather than by a list somebody
/// has to remember to extend: every writable property gets a value that is not its default, the
/// file is written and read back, and anything that did not survive is named.
///
/// Reflection is the point. A hand-written list of settings to check would be exactly as complete
/// as somebody's memory on the day a setting was added, which is the same failure the thing under
/// test has.
/// </summary>
internal static class SavingTests
{
    /// <summary>
    /// A value for this property that is certainly not the one it already holds, or null when this
    /// test does not know how to make one.
    ///
    /// Returning null is a failure rather than a skip: an unhandled type means a new setting of
    /// that type is silently unchecked, which is the hole this whole file exists to close.
    /// </summary>
    private static object? DistinctiveValue(PropertyInfo property, object current)
    {
        var type = property.PropertyType;
        var now = property.GetValue(current);

        if (type == typeof(string)) return "set-by-the-suite";
        if (type == typeof(bool)) return !(bool)now!;
        if (type == typeof(bool?)) return !((bool?)now ?? false);
        if (type == typeof(int)) return (int)now! + 7;
        if (type == typeof(long)) return (long)now! + 7;
        if (type == typeof(double)) return (double)now! + 1.5;

        // HotwordEnergyThreshold is a short, because the samples it is compared against are.
        if (type == typeof(short)) return (short)((short)now! + 7);

        if (type.IsEnum)
        {
            // Any member other than the one it holds, so a default that happens to be the first
            // member is still a real change.
            var members = Enum.GetValues(type).Cast<object>().ToList();
            return members.FirstOrDefault(v => !Equals(v, now)) ?? members.First();
        }

        if (type == typeof(List<string>)) return new List<string> { "alpha", "bravo", "charlie" };

        if (type == typeof(List<RadioConfig>))
        {
            // Filled by the same rules, so a new RadioConfig property is covered too.
            var radio = new RadioConfig();
            foreach (var inner in Writable(typeof(RadioConfig)))
            {
                var value = DistinctiveValue(inner, radio);
                if (value == null) return null;
                inner.SetValue(radio, value);
            }

            return new List<RadioConfig> { radio };
        }

        return null;
    }

    private static List<PropertyInfo> Writable(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Two property values compared as JSON - which handles the lists and the nested radios
    /// without a type switch, and compares them by content rather than by reference.
    /// </summary>
    private static string AsText(object? value) => JsonSerializer.Serialize(value);

    public static void Run()
    {
        Section("Every setting survives a save and a reload");

        var folder = Path.Combine(Path.GetTempPath(), "darkstar-saving-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var path = Path.Combine(folder, "config.json");

            var properties = Writable(typeof(AppConfig));
            Check("the settings were found by reflection", properties.Count > 100, $"{properties.Count}");

            var config = new AppConfig();
            var unhandled = new List<string>();

            foreach (var property in properties)
            {
                var value = DistinctiveValue(property, config);

                if (value == null)
                {
                    unhandled.Add($"{property.Name} ({property.PropertyType.Name})");
                    continue;
                }

                property.SetValue(config, value);
            }

            // A new setting of a type this test cannot build is not checked by it, and nothing
            // else would notice. Failing here is the reminder to extend DistinctiveValue.
            Check("this test knows how to set every one of them", unhandled.Count == 0,
                string.Join(", ", unhandled));

            config.Save(path);
            Check("the file was written", File.Exists(path));

            var reloaded = AppConfig.LoadReadOnly(path);
            Check("and reads back", reloaded != null);

            var lost = properties
                .Where(p => AsText(p.GetValue(config)) != AsText(p.GetValue(reloaded)))
                .Select(p => $"{p.Name}: saved {AsText(p.GetValue(config))}, read back {AsText(p.GetValue(reloaded))}")
                .ToList();

            Check($"all {properties.Count} settings come back exactly as they were saved",
                lost.Count == 0, string.Join(" | ", lost.Take(4)));

            Section("The values really were different from the defaults");

            // Without this the test above would pass just as well against a Save that writes a
            // fresh default config, since "unchanged" and "correctly round-tripped" would look
            // the same. Compared against a default so the previous check is known to be load-bearing.
            var untouched = new AppConfig();
            var sameAsDefault = properties
                .Where(p => AsText(p.GetValue(config)) == AsText(p.GetValue(untouched)))
                .Select(p => p.Name)
                .ToList();

            Check("no setting was left at its default by accident", sameAsDefault.Count == 0,
                string.Join(", ", sameAsDefault));

            Section("A reload picks up what another writer changed");

            // The editor and the bot both read this file, and the profiles copy whole files over
            // it. A loader that cached anything would show stale settings after an activation.
            new AppConfig { BotCallsign = "First" }.Save(path);
            Check("first value", AppConfig.LoadReadOnly(path)!.BotCallsign == "First");

            new AppConfig { BotCallsign = "Second" }.Save(path);
            Check("a second read sees the second value",
                AppConfig.LoadReadOnly(path)!.BotCallsign == "Second",
                AppConfig.LoadReadOnly(path)!.BotCallsign);

            Section("Saving keeps the previous version");

            // Save is destructive and the editor's Save button is one click. Every save backs the
            // file up first, which is what makes a mis-click recoverable.
            var backups = Directory.Exists(Path.Combine(folder, "Backup"))
                ? Directory.GetFiles(Path.Combine(folder, "Backup"), "config*")
                : Array.Empty<string>();
            Check("a backup was written", backups.Length > 0, $"{backups.Length}");
            Check("and one of them holds the version before the last save",
                backups.Any(b => AppConfig.LoadReadOnly(b)?.BotCallsign == "First"),
                string.Join(", ", backups.Select(Path.GetFileName)));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }

        Section("Nothing the editor can change is kept outside those three files");

        // The editor's panels bind either to Store.Config / Store.Phrases / Store.Vocabulary -
        // which SaveAll writes - or to a local field that is deliberately transient (a query form,
        // a service name, the text box for a new entry). A local field holding a SETTING would be
        // edited, shown, and silently dropped on save, with nothing anywhere to say so.
        //
        // So the local ones are listed here by name. The list is not maintenance for its own sake:
        // adding a binding to a new local field fails this check, and that failure is the moment
        // to ask whether the value needs saving.
        var allowedLocalBindings = new[]
        {
            // ServicePanel: what to do to the Windows Service, not what to store.
            "_serviceName", "_exePath", "_displayName",

            // DcsGrpcPanel: the mission-explorer query form and the two test boxes. "_input" is
            // the explorer's whole query object (unit name, coordinates, poll rate) - arguments
            // for a read-only query against the running mission, not settings.
            "_queryId", "_input", "_intelPlayerName", "_intelKind", "_airfieldQuery", "_airfieldKind",

            // The box for an entry that does not exist yet; Add puts it into the saved list.
            "_newTerm",

            // ProfilesPanel: the profile name to save under, and the folder to switch to. Both
            // are acted on by their own buttons - neither belongs in config.json.
            "_newName", "_folder",
        };

        var panelFiles = Directory.GetFiles(
            Path.Combine(Root, "src", "Darkstar.Gui", "Pages"), "*.razor");

        var localBindings = new List<string>();

        foreach (var file in panelFiles.Concat(new[] { Path.Combine(Root, "src", "Darkstar.Gui", "Pages", "Main.razor") }).Distinct())
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex
                         .Matches(File.ReadAllText(file), @"@bind=""([^""]+)"""))
            {
                var target = match.Groups[1].Value;

                // Saved by SaveAll, or a loop variable over one of the saved collections.
                if (target.StartsWith("Store.Config", StringComparison.Ordinal)) continue;
                if (target.StartsWith("radio.", StringComparison.Ordinal)) continue;
                if (target.StartsWith("entry.", StringComparison.Ordinal)) continue;

                // The editor's own folder, saved by UseFolder into the editor's settings.
                if (target == "Store.ConfigFolder") continue;

                // A wrapper property on the panel. Checked below, by its setter.
                if (!target.StartsWith('_')) continue;

                var field = target.Split('.')[0];
                if (!localBindings.Contains(field)) localBindings.Add(field);
            }
        }

        var unexpected = localBindings.Where(b => !allowedLocalBindings.Contains(b)).ToList();
        Check("every local binding in the editor is a known transient one", unexpected.Count == 0,
            unexpected.Count == 0 ? "" : "not accounted for: " + string.Join(", ", unexpected));

        var goneStale = allowedLocalBindings.Where(b => !localBindings.Contains(b)).ToList();
        Check("and the list has no entries left over from a removed field", goneStale.Count == 0,
            string.Join(", ", goneStale));

        Section("Every wrapper property writes back into the config");

        // The trigger lists are edited as one comma-separated line, so each is a property with a
        // getter that joins and a setter that splits. A setter that forgot to assign would accept
        // typing, show it, and lose it on save - the field would simply re-render from the config
        // it never wrote to.
        var wrapperMisses = new List<string>();

        foreach (var file in panelFiles)
        {
            var text = File.ReadAllText(file);

            var bound = System.Text.RegularExpressions.Regex
                .Matches(text, @"@bind=""([A-Z][A-Za-z0-9]*)""")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            foreach (var name in bound)
            {
                // The property declaration through to its closing brace, which is where the
                // setter has to mention the config.
                var declaration = System.Text.RegularExpressions.Regex.Match(
                    text, @"private\s+[\w<>?\[\]]+\s+" + name + @"\s*\{(?<body>[^}]*(\}[^}]*)*?)\n    \}",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (!declaration.Success)
                {
                    wrapperMisses.Add($"{Path.GetFileName(file)}: {name} is bound but not declared");
                    continue;
                }

                var body = declaration.Groups["body"].Value;

                // The SETTER's own text, not the whole property. Looking at the property as a
                // whole is no check at all here: the getter reads Store.Config too, so a setter
                // that had stopped assigning anything still mentioned it and passed. Found by
                // sabotage - removing the assignment from a trigger field fired nothing.
                var setter = System.Text.RegularExpressions.Regex.Match(body,
                    @"set\s*(?:=>\s*(?<expr>[^;]*);|\{(?<block>[^}]*)\})",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (!setter.Success)
                {
                    wrapperMisses.Add($"{Path.GetFileName(file)}: {name} has no setter");
                    continue;
                }

                var setterText = setter.Groups["expr"].Success && setter.Groups["expr"].Value.Length > 0
                    ? setter.Groups["expr"].Value
                    : setter.Groups["block"].Value;

                if (!setterText.Contains("Store.Config", StringComparison.Ordinal))
                    wrapperMisses.Add($"{Path.GetFileName(file)}: {name}'s setter does not assign to Store.Config ({setterText.Trim()})");
            }
        }

        Check("each one has a setter that assigns to Store.Config", wrapperMisses.Count == 0,
            string.Join(" | ", wrapperMisses.Take(4)));
    }
}
