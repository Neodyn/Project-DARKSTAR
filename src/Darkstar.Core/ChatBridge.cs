using System.Globalization;
using System.Text;

namespace Darkstar;

/// <summary>One request a player typed into the DCS chat, as the server hook filed it.</summary>
/// <param name="Player">The player's name in DCS - for an SRS user normally the same as their SRS name.</param>
/// <param name="Ucid">DCS's id for the player's account. Logged, not used for anything else yet.</param>
/// <param name="Side">0 spectator, 1 red, 2 blue - the same numbers as <see cref="AppConfig.Coalition"/>.</param>
/// <param name="Group">The player's group in the mission, which is what the bot locates them by. Empty for a spectator.</param>
/// <param name="GroupId">That group's mission id, for an on-screen answer. 0 when unknown.</param>
/// <param name="Callsign">Which of the bot's callsigns the player addressed, as the bot spells it.</param>
/// <param name="Text">What followed the callsign: "picture", "bogey dope", "Radio check".</param>
public sealed record ChatRequest(
    string Player,
    string Ucid,
    int Side,
    string Group,
    uint GroupId,
    string Callsign,
    string Text);

/// <summary>
/// The Darkstar half of the chat bridge: a player types <c>@Overlord picture</c>, the server hook
/// <c>dcs\Scripts\Hooks\Darkstar-ChatBridge.lua</c> hides the line from everybody else and drops it
/// into a folder, and the bot picks it up from there and answers it like an F10 menu click.
///
/// It is the third way into <see cref="CallCatalog"/>, after the radio and the F10 menu, and the one
/// the radio wheel will use. Two things were measured before any of it was written (chat test,
/// November 2026): a message hidden by the server hook reaches nobody but the server - and the
/// sender, whose own line DCS shows locally before the server is asked. So a request is meant to
/// be readable, because its author will read it.
///
/// Two files tie the halves together, both under one folder the operator points
/// <see cref="AppConfig.ChatBridgeFolder"/> at (<c>&lt;Saved Games of the server&gt;\Darkstar\ChatBridge</c>):
///
/// - <c>callsigns.txt</c>, written here every few seconds. It tells the hook which names count as
///   addressing the bot, and its age tells the hook whether the bot is running at all - with no
///   fresh heartbeat the hook lets every message through rather than swallow one nobody answers.
/// - <c>In\*.txt</c>, one request each, written by the hook as .tmp and renamed.
/// </summary>
public static class ChatBridge
{
    /// <summary>The first line of every request file, so a stray file in the folder is never taken for one.</summary>
    public const string Header = "darkstar-chat 1";

    /// <summary>The hook's file name, for the log and the manuals.</summary>
    public const string HookFileName = "Darkstar-ChatBridge.lua";

    /// <summary>The heartbeat file the hook reads.</summary>
    public const string CallsignFileName = "callsigns.txt";

    /// <summary>Where the hook puts requests, under the bridge folder.</summary>
    public const string InboxFolderName = "In";

    /// <summary>
    /// How often the bot rewrites the heartbeat. The hook gives up on the bot after 60 seconds of
    /// silence, so this leaves room for a slow disk and a busy machine several times over.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often the inbox is looked at. A click on a wheel should not wait for a sweep.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The names a player can address the bot by in the chat: the global callsign and every radio's
    /// own one, each once. The wake words are deliberately not in it - they are tuned for a speech
    /// model, not for somebody typing, and "@Overlord" is what a pilot types.
    /// </summary>
    public static List<string> CallsignsFor(AppConfig config)
    {
        var names = new List<string>();

        void Add(string? name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return;
            if (names.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase))) return;
            names.Add(trimmed);
        }

        Add(config.BotCallsign);
        foreach (var radio in config.Radios ?? new List<RadioConfig>()) Add(radio.Callsign);

        return names;
    }

    /// <summary>What goes into callsigns.txt: a comment line, then one name per line.</summary>
    public static string CallsignFileContent(IEnumerable<string> callsigns)
    {
        var text = new StringBuilder();
        text.Append("# Written by D.A.R.K.S.T.A.R. every few seconds while it runs; read by ")
            .Append(HookFileName).Append(".\n");
        text.Append("# A file older than a minute means the bot is not running, and the hook hides nothing.\n");

        foreach (var name in callsigns)
        {
            var oneLine = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (oneLine.Length > 0) text.Append(oneLine).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Rewrites the heartbeat. Returns null on success, otherwise what went wrong - the caller logs
    /// it once rather than on every beat.
    /// </summary>
    public static string? WriteCallsigns(string folder, IEnumerable<string> callsigns)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, CallsignFileName);
            var temp = path + ".tmp";

            // Written aside and moved over the old one, so the hook never reads half a list - a
            // half list would hide only some of the requests, which is harder to diagnose than none.
            File.WriteAllText(temp, CallsignFileContent(callsigns), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The folder the hook really writes to, from whatever the operator picked.
    ///
    /// The hook always writes to <c>&lt;Saved Games&gt;\Darkstar\ChatBridge</c>. Picking that exact
    /// folder in a file dialog means first finding a folder that may not exist yet, so the obvious
    /// neighbours are accepted as well and turned into it: the Saved Games folder itself, its
    /// <c>Scripts</c> or <c>Scripts\Hooks</c> folder (where the hook was just copied to), and its
    /// <c>Darkstar</c> folder. Pointing at <c>Scripts\Hooks</c> was the first thing that happened in
    /// practice: the bot wrote its heartbeat there, the hook looked for it two folders away, and every
    /// request went through unanswered without a single warning.
    ///
    /// Anything else comes back as it was given, and <see cref="FolderProblem"/> says what is wrong
    /// with it.
    /// </summary>
    public static string ResolveFolder(string? configured)
    {
        var folder = (configured ?? "").Trim().TrimEnd('\\', '/');
        if (folder.Length == 0) return "";

        static bool Named(string? path, string name) =>
            string.Equals(Path.GetFileName(path ?? ""), name, StringComparison.OrdinalIgnoreCase);

        var parent = Path.GetDirectoryName(folder);
        if (Named(folder, "ChatBridge") && Named(parent, "Darkstar")) return folder;

        string? savedGames = null;
        if (Named(folder, "Hooks") && Named(parent, "Scripts")) savedGames = Path.GetDirectoryName(parent);
        else if (Named(folder, "Scripts") || Named(folder, "Darkstar")) savedGames = parent;
        else if (Directory.Exists(Path.Combine(folder, "Scripts"))) savedGames = folder;

        return string.IsNullOrEmpty(savedGames) ? folder : Path.Combine(savedGames, "Darkstar", "ChatBridge");
    }

    /// <summary>
    /// What is wrong with the configured folder, or null when nothing is. Checks the folder the bot
    /// will really use (<see cref="ResolveFolder"/>): that it is the one the hook writes to, and that
    /// the hook is installed beside it. Both mistakes are otherwise silent - hook and bot run happily
    /// in two different places, and the pilot simply hears nothing.
    /// </summary>
    public static string? FolderProblem(string? configured)
    {
        var folder = ResolveFolder(configured);
        if (folder.Length == 0) return null;

        var hook = ExpectedHookPath(folder);
        if (hook == null)
            return $"{folder} is not <Saved Games of the DCS server>\\Darkstar\\ChatBridge, the folder {HookFileName} writes to.";

        return File.Exists(hook)
            ? null
            : $"{HookFileName} is not at {hook} - either the hook is not installed on this server, " +
              "or the folder points into the wrong Saved Games folder.";
    }

    /// <summary>
    /// Where the hook has to be for the bridge folder to be its folder: the hook writes to
    /// <c>&lt;Saved Games&gt;\Darkstar\ChatBridge</c>, so it lives two levels up, in
    /// <c>&lt;Saved Games&gt;\Scripts\Hooks</c>. Null when the folder, once resolved, is not a
    /// <c>Darkstar\ChatBridge</c> folder at all - then there is no right place for the hook.
    /// </summary>
    public static string? ExpectedHookPath(string? folder)
    {
        var resolved = ResolveFolder(folder);
        if (resolved.Length == 0) return null;

        var darkstar = Path.GetDirectoryName(resolved);
        if (!string.Equals(Path.GetFileName(resolved), "ChatBridge", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(darkstar ?? ""), "Darkstar", StringComparison.OrdinalIgnoreCase))
            return null;

        var savedGames = Path.GetDirectoryName(darkstar);
        if (string.IsNullOrEmpty(savedGames)) return null;

        return Path.Combine(savedGames, "Scripts", "Hooks", HookFileName);
    }

    /// <summary>Reads one request file. Null for anything that is not one.</summary>
    public static ChatRequest? Parse(string? content)
    {
        var lines = (content ?? "").Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header) return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            // Split at the first '=' only: a request text may well contain one.
            var at = line.IndexOf('=');
            if (at <= 0) continue;
            values[line[..at].Trim()] = line[(at + 1)..].Trim();
        }

        string Get(string key) => values.TryGetValue(key, out var value) ? value : "";

        var player = Get("player");
        var callsign = Get("callsign");
        if (player.Length == 0 || callsign.Length == 0) return null;

        int.TryParse(Get("side"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var side);
        uint.TryParse(Get("groupid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupId);

        return new ChatRequest(player, Get("ucid"), side, Get("group"), groupId, callsign, Get("text"));
    }

    /// <summary>
    /// Which call a chat request asks for. Matched as a whole against the menu label, the id and
    /// the spoken phrase of every call the F10 menu offers - "Picture", "picture", "bogey dope",
    /// "BOGEY_DOPE", "vector to nearest" - ignoring case and punctuation. Null when nothing fits.
    ///
    /// Only calls the menu offers, on purpose: those are exactly the ones that can be answered
    /// without anything after the request ("spike" needs a bearing, "declare" a point), and exactly
    /// the ones <c>AnswerMenuCallAsync</c> in the bot knows how to answer from a group alone.
    /// </summary>
    public static CallDefinition? MatchCall(string? text)
    {
        var wanted = Normalise(text);
        if (wanted.Length == 0) return null;

        return CallCatalog.All
            .Where(CallCatalog.MenuOffers)
            .FirstOrDefault(call =>
                Normalise(call.MenuLabel) == wanted ||
                Normalise(call.Id) == wanted ||
                Normalise(call.EchoPhrase) == wanted);
    }

    /// <summary>Lower case, punctuation and underscores to spaces, runs of spaces to one.</summary>
    internal static string Normalise(string? text)
    {
        var cleaned = new StringBuilder();
        foreach (var c in (text ?? "").ToLowerInvariant())
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');

        return string.Join(' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>What one look at the inbox found.</summary>
    /// <param name="Fresh">Requests to answer, oldest first.</param>
    /// <param name="Stale">Files dropped for being older than the limit, by name.</param>
    /// <param name="Broken">Files dropped because they were not a request, by name.</param>
    public sealed record Batch(List<ChatRequest> Fresh, List<string> Stale, List<string> Broken);

    /// <summary>
    /// Takes every finished request out of the inbox. Every file is deleted once read, answered or
    /// not - a file left behind would be answered again on the next look.
    ///
    /// Requests older than <paramref name="maxAge"/> are dropped instead of answered. The age comes
    /// from the file, which the hook wrote on the same machine a moment ago; the clock of the
    /// player's PC is never involved, and in the chat test it was 24 seconds off. Without the
    /// limit, a bot restarted after a crash would answer a minute's worth of requests in one burst,
    /// most of them from pilots who have long since asked again or given up.
    /// </summary>
    public static Batch Collect(string folder, DateTime nowUtc, TimeSpan maxAge)
    {
        var batch = new Batch(new List<ChatRequest>(), new List<string>(), new List<string>());
        var inbox = Path.Combine(folder, InboxFolderName);
        if (!Directory.Exists(inbox)) return batch;

        // The hook names its files "<unix time>-<counter>", so ordinal order is arrival order.
        var files = Directory.GetFiles(inbox, "*.txt").OrderBy(f => f, StringComparer.Ordinal).ToList();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            string content;
            DateTime written;

            try
            {
                written = File.GetLastWriteTimeUtc(file);
                content = File.ReadAllText(file);
                File.Delete(file);
            }
            catch (IOException)
            {
                // Still being renamed into place, or held by a virus scanner for a moment. It is
                // still there on the next look a quarter of a second from now.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (nowUtc - written > maxAge)
            {
                batch.Stale.Add(name);
                continue;
            }

            var request = Parse(content);
            if (request == null) batch.Broken.Add(name);
            else batch.Fresh.Add(request);
        }

        return batch;
    }
}
