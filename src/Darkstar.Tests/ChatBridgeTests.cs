using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The chat bridge: a request typed into the DCS chat, filed by the server hook, answered by the bot.
///
/// Its two halves never meet in one process - one is Lua inside DCS, the other C# - so the place
/// they can drift apart is the file between them: a key renamed on one side, a folder spelled
/// differently, a heartbeat slower than the hook's patience. Those are pinned down here against the
/// hook's source, because the failure they cause is silence, and silence looks like "DCS chat is
/// flaky" rather than like a bug.
/// </summary>
internal static class ChatBridgeTests
{
    public static void Run()
    {
        Section("Reading a request file");

        var file = string.Join("\n",
            "darkstar-chat 1",
            "player=Titan 1-1 | Ghost",
            "ucid=abc123",
            "side=2",
            "group=Titan 1",
            "groupid=42",
            "callsign=Overlord",
            "to=-2",
            "text=picture");

        var parsed = ChatBridge.Parse(file);
        Check("a hook-written file is a request", parsed != null);
        Check("with every field", parsed is { Player: "Titan 1-1 | Ghost", Ucid: "abc123", Side: 2, Group: "Titan 1", GroupId: 42, Callsign: "Overlord", Text: "picture" },
            parsed?.ToString() ?? "null");

        Check("Windows line endings change nothing",
            ChatBridge.Parse(file.Replace("\n", "\r\n")) == parsed);

        Check("a '=' inside the text is kept - only the first one splits",
            ChatBridge.Parse(file.Replace("text=picture", "text=a=b"))?.Text == "a=b");

        Check("without the header line it is not a request",
            ChatBridge.Parse(file.Replace("darkstar-chat 1\n", "")) == null);
        Check("a later version of the format is not read as this one",
            ChatBridge.Parse(file.Replace("darkstar-chat 1", "darkstar-chat 2")) == null);
        Check("nor without a player",
            ChatBridge.Parse(file.Replace("player=Titan 1-1 | Ghost", "player=")) == null);
        Check("nor without a callsign",
            ChatBridge.Parse(file.Replace("callsign=Overlord", "callsign=")) == null);
        Check("an empty file is nothing", ChatBridge.Parse("") == null && ChatBridge.Parse(null) == null);

        // A spectator has no slot, so the hook writes the group fields empty.
        var spectator = ChatBridge.Parse(file.Replace("group=Titan 1", "group=").Replace("groupid=42", "groupid="));
        Check("a spectator's empty group reads as empty and 0",
            spectator is { Group: "", GroupId: 0u }, spectator?.ToString() ?? "null");
        Check("a side that is not a number reads as 0, the spectator side",
            ChatBridge.Parse(file.Replace("side=2", "side=blue"))?.Side == 0);

        Section("Which call a typed request asks for");

        void Means(string typed, string? id) =>
            Eq($"\"{typed}\"", ChatBridge.MatchCall(typed)?.Id ?? "(none)", id ?? "(none)");

        Means("picture", "picture");
        Means("Picture", "picture");                 // what the wheel sends: the menu label
        Means("  PICTURE! ", "picture");
        Means("bogey dope", "bogey_dope");
        Means("bogey_dope", "bogey_dope");           // the id
        Means("Bogey-dope", "bogey_dope");
        Means("threat check", "threat_check");
        Means("alpha check", "alpha_check");
        Means("runway in use", "runway_in_use");
        Means("ATIS", "atis");
        Means("divert", "divert");
        Means("vector to nearest", "divert");        // the spoken phrase
        Means("radio check", "radio_check");

        // Only what the F10 menu offers: everything else needs an argument or is keyed by SRS name.
        Means("spike", null);
        Means("declare", null);
        Means("checking in", null);
        Means("threat circle on", null);
        Means("say again", null);

        // Whole phrases only, so a sentence that merely contains a call word is not taken for one.
        Means("picture please", null);
        Means("what is the picture", null);
        Means("", null);
        Means("?!", null);

        // Every call the menu offers can be typed by its label, so the wheel - which sends labels -
        // can never offer a slice the bot does not understand.
        var unreachable = CallCatalog.All
            .Where(CallCatalog.MenuOffers)
            .Where(call => ChatBridge.MatchCall(call.MenuLabel)?.Id != call.Id)
            .Select(call => call.Id)
            .ToList();
        Check("every menu call is reachable by its label", unreachable.Count == 0, string.Join(", ", unreachable));

        // And the bot can answer every one of them from a group alone. AnswerMenuCallAsync handles
        // exactly these kinds; a call the menu starts offering one day without an answer behind it
        // would be accepted from the chat and then answered with nothing.
        var answerable = new[]
        {
            CallKind.RadioCheck, CallKind.RunwayInUse, CallKind.Atis, CallKind.Divert,
            CallKind.BogeyDope, CallKind.Picture, CallKind.ThreatCheck, CallKind.AlphaCheck,
        };
        var unanswerable = CallCatalog.All
            .Where(CallCatalog.MenuOffers)
            .Where(call => !answerable.Contains(call.Kind))
            .Select(call => call.Id)
            .ToList();
        Check("and every one of them has an answer behind it", unanswerable.Count == 0, string.Join(", ", unanswerable));

        Section("The names a pilot can address the bot by");

        var config = new AppConfig
        {
            BotCallsign = "Overlord",
            Radios = new List<RadioConfig>
            {
                new() { Callsign = "" },                 // uses the global one
                new() { Callsign = " Batumi Tower " },
                new() { Callsign = "overlord" },         // the same name, other case
                new() { Callsign = "Magic" },
            },
        };
        Eq("the global callsign, then every radio's own, each once",
            string.Join(" | ", ChatBridge.CallsignsFor(config)), "Overlord | Batumi Tower | Magic");

        var content = ChatBridge.CallsignFileContent(new[] { "Overlord", "Batumi\nTower", " " });
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check("the heartbeat file opens with comments the hook skips", lines.Length > 0 && lines[0].StartsWith('#'));
        Eq("and holds one name per line, never a name broken over two",
            string.Join(" | ", lines.Where(l => !l.StartsWith('#'))), "Overlord | Batumi Tower");

        Section("The heartbeat file");

        var temp = Path.Combine(Path.GetTempPath(), "darkstar-chat-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var folder = Path.Combine(temp, "Darkstar", "ChatBridge");

            Check("is written, creating the folder", ChatBridge.WriteCallsigns(folder, new[] { "Overlord" }) == null);
            var written = Path.Combine(folder, ChatBridge.CallsignFileName);
            Check("under the name the hook reads", File.Exists(written));
            Check("and rewritten in place", ChatBridge.WriteCallsigns(folder, new[] { "Magic" }) == null
                && File.ReadAllText(written).Contains("Magic") && !File.ReadAllText(written).Contains("Overlord"));
            Check("leaving no temporary file behind",
                Directory.GetFiles(folder).Select(Path.GetFileName).SequenceEqual(new[] { ChatBridge.CallsignFileName }),
                string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName)));

            // A file where the folder should be: the error comes back instead of being thrown, so
            // the bot can log it once rather than die in its polling loop.
            var blocked = Path.Combine(temp, "blocked");
            File.WriteAllText(blocked, "");
            Check("a folder that cannot be created is reported, not thrown",
                ChatBridge.WriteCallsigns(Path.Combine(blocked, "ChatBridge"), new[] { "Overlord" }) != null);

            Section("Taking requests out of the inbox");

            var inbox = Path.Combine(folder, ChatBridge.InboxFolderName);
            Directory.CreateDirectory(inbox);
            var now = DateTime.UtcNow;

            void Put(string name, string body, double ageSeconds)
            {
                var path = Path.Combine(inbox, name);
                File.WriteAllText(path, body);
                File.SetLastWriteTimeUtc(path, now - TimeSpan.FromSeconds(ageSeconds));
            }

            Put("1794854815-00002.txt", file.Replace("text=picture", "text=bogey dope"), 1);
            Put("1794854815-00001.txt", file, 1);
            Put("1794854700-00009.txt", file.Replace("text=picture", "text=old"), 60);
            Put("1794854816-00003.txt", "not a request", 1);
            Put("1794854816-00004.tmp", file, 1);   // still being written by the hook

            var batch = ChatBridge.Collect(folder, now, TimeSpan.FromSeconds(15));

            Eq("fresh requests in the order the hook filed them",
                string.Join(" | ", batch.Fresh.Select(r => r.Text)), "picture | bogey dope");
            Eq("an old one is dropped, by name", string.Join(" | ", batch.Stale), "1794854700-00009.txt");
            Eq("a file that is not a request is dropped, by name", string.Join(" | ", batch.Broken), "1794854816-00003.txt");
            Eq("every finished file is gone - a file left behind is answered twice",
                string.Join(" | ", Directory.GetFiles(inbox).Select(Path.GetFileName)), "1794854816-00004.tmp");

            var again = ChatBridge.Collect(folder, now, TimeSpan.FromSeconds(15));
            Check("so the next look finds nothing to answer", again.Fresh.Count == 0 && again.Stale.Count == 0);

            var nothing = ChatBridge.Collect(Path.Combine(temp, "nope"), now, TimeSpan.FromSeconds(15));
            Check("a folder the hook never wrote to is simply empty", nothing.Fresh.Count == 0);

            Section("Where the hook has to be");

            var savedGames = Path.Combine(temp, "Saved Games", "DCS.dcs_serverrelease");
            var bridge = Path.Combine(savedGames, "Darkstar", "ChatBridge");
            var hook = Path.Combine(savedGames, "Scripts", "Hooks", ChatBridge.HookFileName);

            Eq("two levels up from the folder, in Scripts\\Hooks", ChatBridge.ExpectedHookPath(bridge) ?? "null", hook);
            Eq("a trailing separator makes no difference",
                ChatBridge.ExpectedHookPath(bridge + Path.DirectorySeparatorChar) ?? "null", hook);
            Check("an empty folder has no hook path", ChatBridge.ExpectedHookPath("  ") == null);

            Section("Picking a neighbour of the folder is fine");

            // The first real setup pointed ChatBridgeFolder at Scripts\Hooks, where the hook had just
            // been copied to. The bot wrote its heartbeat there, the hook looked two folders away,
            // and every request went through unanswered - with no warning, because the hook WAS two
            // levels above that folder. So the neighbours are turned into the real folder.
            Directory.CreateDirectory(Path.Combine(savedGames, "Scripts", "Hooks"));

            Eq("the exact folder stays as it is", ChatBridge.ResolveFolder(bridge), bridge);
            Eq("the Hooks folder the hook was copied to", ChatBridge.ResolveFolder(Path.Combine(savedGames, "Scripts", "Hooks")), bridge);
            Eq("the Scripts folder", ChatBridge.ResolveFolder(Path.Combine(savedGames, "Scripts")), bridge);
            Eq("the Darkstar folder", ChatBridge.ResolveFolder(Path.Combine(savedGames, "Darkstar")), bridge);
            Eq("the Saved Games folder itself, recognised by its Scripts folder", ChatBridge.ResolveFolder(savedGames), bridge);
            Eq("with a trailing separator too",
                ChatBridge.ResolveFolder(Path.Combine(savedGames, "Scripts", "Hooks") + Path.DirectorySeparatorChar), bridge);

            var elsewhere = Path.Combine(temp, "somewhere else");
            Eq("anything else is left alone", ChatBridge.ResolveFolder(elsewhere), elsewhere);
            Check("and has no place for a hook", ChatBridge.ExpectedHookPath(elsewhere) == null);
            Check("which is reported, rather than passing quietly",
                ChatBridge.FolderProblem(elsewhere)?.Contains("is not <Saved Games", StringComparison.Ordinal) == true,
                ChatBridge.FolderProblem(elsewhere) ?? "null");

            Section("The startup warnings");

            List<string> WarningsFor(AppConfig c)
            {
                var found = new List<string>();
                AppConfig.ValidateValues(c, found.Add);
                return found.Where(w => w.Contains("ChatBridge", StringComparison.Ordinal)).ToList();
            }

            Check("nothing is said while the bridge is off",
                WarningsFor(new AppConfig { ChatBridgeEnabled = false, ChatBridgeFolder = "" }).Count == 0);

            var noFolder = WarningsFor(new AppConfig { ChatBridgeEnabled = true, DcsGrpcEnabled = true, ChatBridgeFolder = "" });
            Check("on without a folder says so", noFolder.Any(w => w.Contains("ChatBridgeFolder is empty")), string.Join(" || ", noFolder));

            var noHook = WarningsFor(new AppConfig { ChatBridgeEnabled = true, DcsGrpcEnabled = true, ChatBridgeFolder = bridge });
            Check("a folder whose hook is missing names where it should be",
                noHook.Any(w => w.Contains(hook, StringComparison.Ordinal)), string.Join(" || ", noHook));

            Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            File.WriteAllText(hook, "-- hook");
            var withHook = WarningsFor(new AppConfig { ChatBridgeEnabled = true, DcsGrpcEnabled = true, ChatBridgeFolder = bridge });
            Check("and is quiet once the hook is there", withHook.Count == 0, string.Join(" || ", withHook));

            var hooksPicked = WarningsFor(new AppConfig
            {
                ChatBridgeEnabled = true, DcsGrpcEnabled = true,
                ChatBridgeFolder = Path.Combine(savedGames, "Scripts", "Hooks"),
            });
            Check("quiet too when the Hooks folder was picked, since that is turned into the right one",
                hooksPicked.Count == 0, string.Join(" || ", hooksPicked));

            var wrongPlace = WarningsFor(new AppConfig { ChatBridgeEnabled = true, DcsGrpcEnabled = true, ChatBridgeFolder = elsewhere });
            Check("but not when the folder is nowhere near the hook",
                wrongPlace.Any(w => w.Contains("is not <Saved Games", StringComparison.Ordinal)), string.Join(" || ", wrongPlace));

            var noGrpc = WarningsFor(new AppConfig { ChatBridgeEnabled = true, DcsGrpcEnabled = false, ChatBridgeFolder = bridge });
            Check("without DCS-gRPC it says only the radio check works",
                noGrpc.Any(w => w.Contains("only the radio check", StringComparison.Ordinal)), string.Join(" || ", noGrpc));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* a temp folder left behind is harmless */ }
        }

        Section("The hook and the bot agree on the file between them");

        var lua = ReadSource("dcs/Scripts/Hooks/" + ChatBridge.HookFileName);

        Check("the hook ships in the repository", lua.Length > 0);
        Check("it writes the header the bot reads", lua.Contains($"'{ChatBridge.Header}\\n'", StringComparison.Ordinal));
        foreach (var key in new[] { "player", "ucid", "side", "group", "groupid", "callsign", "text" })
            Check($"it writes \"{key}=\"", lua.Contains($"'{key}='", StringComparison.Ordinal));

        Check("it reads the heartbeat under the bot's name for it",
            lua.Contains($"'\\\\{ChatBridge.CallsignFileName}'", StringComparison.Ordinal));
        Check("and files into the inbox the bot empties",
            lua.Contains($"'\\\\{ChatBridge.InboxFolderName}'", StringComparison.Ordinal));
        Check("both under Darkstar\\ChatBridge, which is what ExpectedHookPath counts back from",
            lua.Contains("'Darkstar'", StringComparison.Ordinal) && lua.Contains("'\\\\ChatBridge'", StringComparison.Ordinal));

        // The hook gives up on the bot after STALE_AFTER seconds without a heartbeat. Three missed
        // beats is the margin: one slow disk write must not make the hook stop hiding requests.
        var staleAfter = System.Text.RegularExpressions.Regex.Match(lua, @"local STALE_AFTER = (\d+)");
        Check("the hook's patience was found", staleAfter.Success);
        if (staleAfter.Success)
            Check("and outlasts three heartbeats",
                ChatBridge.HeartbeatInterval.TotalSeconds * 3 <= int.Parse(staleAfter.Groups[1].Value),
                $"heartbeat {ChatBridge.HeartbeatInterval.TotalSeconds}s, patience {staleAfter.Groups[1].Value}s");

        Check("it accepts the marker the wheel sends, as bytes", lua.Contains("'\\194\\187'", StringComparison.Ordinal));
        Check("and the two a keyboard has", lua.Contains("'>>'", StringComparison.Ordinal) && lua.Contains("'@'", StringComparison.Ordinal));
        Check("it hides a request only on the server",
            lua.Contains("pcall(DCS.isServer)", StringComparison.Ordinal));

        Section("The bot answers a chat request like a click, under the same rules as a call");

        // Comments stripped first: a comment that merely mentions the rate limiter must not pass
        // for the call to it.
        var bot = string.Join("\n", ReadSource("src/Darkstar.Bot/BotService.cs")
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var start = bot.IndexOf("async Task AnswerChatRequestAsync(", StringComparison.Ordinal);
        var end = start < 0 ? -1 : bot.IndexOf("var radioSummary", start, StringComparison.Ordinal);
        Check("the handler was found", start > 0 && end > start);

        var handler = start > 0 && end > start ? bot[start..end] : "";
        Check("the opposing coalition is not served",
            handler.Contains("config.RestrictToOwnCoalition", StringComparison.Ordinal));
        Check("the rate limit applies", handler.Contains("rateLimiter.Check(request.Player", StringComparison.Ordinal));
        Check("the request goes through the F10 answer, with the player's name and side",
            handler.Contains("await AnswerMenuCallAsync(call, request.Group, request.GroupId", StringComparison.Ordinal)
            && handler.Contains("rawPlayerName: request.Player", StringComparison.Ordinal)
            && handler.Contains("requesterCoalition: request.Side", StringComparison.Ordinal));

        Check("the inbox is emptied by the bot's polling loop", bot.Contains("ChatBridge.Collect(bridgeFolder", StringComparison.Ordinal));
        Check("and the heartbeat is written in it", bot.Contains("ChatBridge.WriteCallsigns(bridgeFolder", StringComparison.Ordinal));
        Check("into the folder the hook writes to, not simply the one that was typed",
            bot.Contains("var bridgeFolder = ChatBridge.ResolveFolder(config.ChatBridgeFolder);", StringComparison.Ordinal));
        Check("only when the bridge is switched on", bot.Contains("if (config.ChatBridgeEnabled)", StringComparison.Ordinal));
    }
}
