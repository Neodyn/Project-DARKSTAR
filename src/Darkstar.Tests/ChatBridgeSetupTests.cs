using System.Text;
using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The editor's setup for the chat bridge (<see cref="ChatBridgeSetup"/>): finding the DCS Saved
/// Games folders, telling a server's from a pilot's, and installing the hook so that it and
/// <c>ChatBridgeFolder</c> agree - which is the one thing that went wrong when it was done by hand.
/// </summary>
internal static class ChatBridgeSetupTests
{
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "darkstar-hooksetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Touch(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static void Run()
    {
        Section("The hook the editor installs is the hook in the repository");

        var shipped = File.ReadAllBytes(Path.Combine(Root, "dcs", "Scripts", "Hooks", ChatBridge.HookFileName));
        Check("it is built into Darkstar.Core", ChatBridgeSetup.HookScript.Length > 100,
            $"{ChatBridgeSetup.HookScript.Length} bytes");
        Check("and is the same script as the file next to Darkstar.exe",
            ChatBridgeSetup.SameScript(ChatBridgeSetup.HookScript, shipped));
        Check("line endings alone do not make a hook \"older\"",
            ChatBridgeSetup.SameScript(Encoding.UTF8.GetBytes("a\nb\n"), Encoding.UTF8.GetBytes("a\r\nb\r\n")));
        Check("a changed line does",
            !ChatBridgeSetup.SameScript(Encoding.UTF8.GetBytes("a\nb\n"), Encoding.UTF8.GetBytes("a\nc\n")));

        Section("Finding the DCS folders, the server's first");

        var users = NewFolder();
        try
        {
            var mine = Path.Combine(users, "pilot", "Saved Games");
            var server = Path.Combine(users, "dcsserver", "Saved Games");

            // A pilot's DCS, run recently.
            Touch(Path.Combine(mine, "DCS", "Config", "options.lua"));
            Touch(Path.Combine(mine, "DCS", "Logs", "dcs.log"));

            // The dedicated server, under its own account, with DCS-gRPC.
            Touch(Path.Combine(server, "DCS.dcs_serverrelease", "Config", "serverSettings.lua"));
            Touch(Path.Combine(server, "DCS.dcs_serverrelease", "Scripts", "Hooks", "DCS-gRPC.lua"));
            File.SetLastWriteTimeUtc(Path.Combine(server, "DCS.dcs_serverrelease", "Config", "serverSettings.lua"), DateTime.UtcNow.AddDays(-3));

            // A server started with "-w Training": no DCS in the name, found by its files.
            Touch(Path.Combine(server, "Training", "Config", "serverSettings.lua"));

            // Not DCS at all.
            Touch(Path.Combine(mine, "Elite Dangerous", "Options", "x.txt"));
            Directory.CreateDirectory(Path.Combine(users, "Public"));
            Directory.CreateDirectory(Path.Combine(users, "nobody"));

            var unreadable = new List<string>();
            var roots = ChatBridgeSetup.SavedGamesRoots(mine, users, unreadable);
            Check("this user's Saved Games and every other profile's are searched",
                roots.Count == 2 && roots.Contains(mine) && roots.Contains(server), string.Join(" | ", roots));
            Check("this user's is not searched twice", roots.Count(r => r == mine) == 1);

            var scan = ChatBridgeSetup.Scan(roots);
            var names = scan.Folders.Select(f => f.Name).ToList();
            Eq("three DCS folders, the dedicated server first", string.Join(", ", names), "DCS.dcs_serverrelease, Training, DCS");
            Check("a game that is not DCS is left out", !names.Contains("Elite Dangerous"));

            var dedicated = scan.Folders[0];
            Check("the dedicated server is recognised by its name", dedicated.DedicatedServer);
            Check("DCS-gRPC's hook is noticed", dedicated.HasGrpcHook);
            Check("and whose profile it is", dedicated.Profile == "dcsserver", dedicated.Profile);
            Check("a -w server is recognised by its settings, not its name",
                scan.Folders[1].HasServerSettings && !scan.Folders[1].DedicatedServer);
            Check("the pilot's DCS is no server", !scan.Folders[2].DedicatedServer && !scan.Folders[2].HasServerSettings);
            Check("it knows when that DCS last ran", scan.Folders[2].LastUsedUtc != null);
            Check("no hook anywhere yet", scan.Folders.All(f => f.Hook == HookState.Missing));

            Section("Installing makes hook and setting agree");

            var hook = ChatBridgeSetup.Install(dedicated.Path);
            Eq("the hook goes into Scripts\\Hooks of that folder", hook, dedicated.HookPath);
            Check("it is this build's hook", ChatBridgeSetup.HookStateOf(dedicated.Path) == HookState.Current);
            Check("next to DCS-gRPC's, which is left alone",
                File.Exists(Path.Combine(dedicated.Path, "Scripts", "Hooks", "DCS-gRPC.lua")));
            Check("the folder it writes to exists already", Directory.Exists(dedicated.BridgeFolder));
            Check("no temporary file is left", !File.Exists(hook + ".tmp"));

            // The pair the bot and the editor check at startup: the folder the setting names must
            // have the hook exactly where ChatBridge expects it. This is the mistake the setup is
            // there to make impossible.
            Eq("the folder it sets is the one the bot will use as it is",
                ChatBridge.ResolveFolder(dedicated.BridgeFolder), dedicated.BridgeFolder);
            Eq("and the hook is where the bot looks for it",
                ChatBridge.ExpectedHookPath(dedicated.BridgeFolder) ?? "-", hook);
            Check("so the bot's startup check finds nothing wrong",
                ChatBridge.FolderProblem(dedicated.BridgeFolder) == null, ChatBridge.FolderProblem(dedicated.BridgeFolder) ?? "");

            File.WriteAllText(hook, "-- an older hook\n");
            Check("a different hook counts as older", ChatBridgeSetup.HookStateOf(dedicated.Path) == HookState.Outdated);
            ChatBridgeSetup.Install(dedicated.Path);
            Check("installing again brings it up to date", ChatBridgeSetup.HookStateOf(dedicated.Path) == HookState.Current);

            Check("removing takes the hook out", ChatBridgeSetup.Remove(dedicated.Path) && !File.Exists(hook));
            Check("and nothing else", File.Exists(Path.Combine(dedicated.Path, "Scripts", "Hooks", "DCS-gRPC.lua"))
                                     && Directory.Exists(dedicated.BridgeFolder));
            Check("removing twice is no error", !ChatBridgeSetup.Remove(dedicated.Path));

            Section("A folder picked by hand");

            Eq("the Saved Games folder itself", ChatBridgeSetup.SavedGamesFolderOf(dedicated.Path), dedicated.Path);
            Eq("its Scripts\\Hooks folder means the Saved Games folder above it",
                ChatBridgeSetup.SavedGamesFolderOf(Path.Combine(dedicated.Path, "Scripts", "Hooks")), dedicated.Path);
            Eq("so does its Darkstar\\ChatBridge folder",
                ChatBridgeSetup.SavedGamesFolderOf(dedicated.BridgeFolder), dedicated.Path);

            var empty = Path.Combine(users, "nobody");
            var picked = ChatBridgeSetup.Inspect(empty);
            Check("a folder without DCS's files is shown as such, not refused", !picked.LooksLikeDcs);
        }
        finally
        {
            try { Directory.Delete(users, recursive: true); } catch { /* best effort */ }
        }

        Section("The editor's setup does both halves");

        var panel = ReadSource("src/Darkstar.Gui/Pages/DcsGrpcPanel.razor");
        var installAt = panel.IndexOf("ChatBridgeSetup.Install(dcs.Path)", StringComparison.Ordinal);
        var settingAt = panel.IndexOf("Store.Config.ChatBridgeFolder = dcs.BridgeFolder;", StringComparison.Ordinal);
        Check("installing the hook also sets the folder it writes to",
            installAt > 0 && settingAt > installAt, $"install at {installAt}, setting at {settingAt}");
        Check("and switches the chat bridge on",
            panel.Contains("Store.Config.ChatBridgeEnabled = true;", StringComparison.Ordinal));
        Check("the search only runs when asked for",
            !panel.Contains("ChatBridgeSetup.Scan()", StringComparison.Ordinal) ||
            panel.IndexOf("ChatBridgeSetup.Scan()", StringComparison.Ordinal) > panel.IndexOf("private void ScanDcsFolders()", StringComparison.Ordinal));
    }
}
