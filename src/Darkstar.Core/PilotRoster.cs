using System.Collections.Concurrent;

namespace Darkstar;

/// <summary>What a pilot asked for that is about them rather than about the enemy.</summary>
public enum RosterRequestKind
{
    None,

    /// <summary>"Darkstar, Punch one one checking in."</summary>
    CheckIn,

    /// <summary>"Darkstar, Punch one one checking out."</summary>
    CheckOut,

    /// <summary>"Say again" - the last thing said to this pilot, repeated.</summary>
    SayAgain,
}

/// <summary>
/// Who is on station, and what they were last told.
///
/// Both exist for the same reason: radio is lossy. A reply arrives while the pilot is pulling
/// through the horizon or has a missile warning going, and the information is gone. "Say again" is
/// the standard fix, and it costs nothing to support because the bot already knows what it said.
///
/// Checking in is a courtesy on a real net. Here it earns its keep as the switch for unprompted
/// calls: a pilot who never checked in may not want a threat circle talking at them. That gate is
/// off by default, because a pilot who simply does not know the phrase would otherwise lose a
/// warning they used to get, and lose it silently.
///
/// Keyed by the pilot's SRS name, which is what every path into this class actually has. Thread
/// safe because the SRS receive loop, the F10 click stream and the threat circle sweep all reach
/// it from different threads.
/// </summary>
public sealed class PilotRoster
{
    /// <summary>
    /// How many pilots are remembered. A busy server cycles through names all evening, and
    /// without a cap the two dictionaries would grow for as long as the process runs. The oldest
    /// entry goes when the cap is reached: whoever has not spoken for longest is the one least
    /// likely to say "say again" next.
    /// </summary>
    public const int MaxPilots = 200;

    private sealed class Entry
    {
        public bool CheckedIn;
        public string? LastReply;
        public DateTime LastSeenUtc = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _pilots = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppConfig _config;

    public PilotRoster(AppConfig config) => _config = config;

    /// <summary>
    /// Which of these three a transmission is, if any. Separate from the tactical classifier
    /// because none of these need DCS at all - they are answerable with the server unplugged,
    /// which is also when a pilot is most likely to be asking.
    /// </summary>
    public RosterRequestKind Classify(string? transcript, out string? matchedTrigger)
    {
        matchedTrigger = null;
        if (string.IsNullOrWhiteSpace(transcript)) return RosterRequestKind.None;

        // "Say again" first. It is the one phrase whose whole purpose is to be understood when
        // the rest of the transmission was not, so nothing else may swallow it.
        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelSayAgainTriggers);
        if (matchedTrigger != null) return RosterRequestKind.SayAgain;

        // Check OUT before check IN: "checking out" contains neither "check in" nor "checking
        // in", but the two lists are editable, and a server owner who adds "check" to one of them
        // should get the more specific of the two rather than whichever was tested first.
        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelCheckOutTriggers);
        if (matchedTrigger != null) return RosterRequestKind.CheckOut;

        matchedTrigger = TriggerMatcher.FindMatch(transcript, _config.DcsIntelCheckInTriggers);
        if (matchedTrigger != null) return RosterRequestKind.CheckIn;

        matchedTrigger = null;
        return RosterRequestKind.None;
    }

    /// <summary>Marks the pilot on station and returns what to say back.</summary>
    public string CheckIn(string? pilot)
    {
        Touch(pilot).CheckedIn = true;
        return Fill(_config.DcsIntelCheckInReply, pilot);
    }

    /// <summary>
    /// Marks the pilot on station without saying anything back.
    ///
    /// For a pilot who has made it obvious without using the words: asking for a standing threat
    /// watch is a pilot putting themselves on station, and making them also say "checking in"
    /// before the watch they just asked for is allowed to talk to them would be a trap.
    /// </summary>
    public void MarkOnStation(string? pilot) => Touch(pilot).CheckedIn = true;

    /// <summary>Marks the pilot off station and returns what to say back.</summary>
    public string CheckOut(string? pilot)
    {
        Touch(pilot).CheckedIn = false;
        return Fill(_config.DcsIntelCheckOutReply, pilot);
    }

    /// <summary>
    /// Whether this pilot may be called unprompted. With the gate off - the default - everybody
    /// may, which is the behaviour the bot had before check-in existed.
    /// </summary>
    public bool MayBeCalledUnprompted(string? pilot)
    {
        if (!_config.DcsIntelCheckInGatesCalls) return true;
        return !string.IsNullOrWhiteSpace(pilot)
               && _pilots.TryGetValue(pilot.Trim(), out var entry)
               && entry.CheckedIn;
    }

    /// <summary>Whether this pilot has checked in, regardless of whether the gate is on.</summary>
    public bool IsCheckedIn(string? pilot) =>
        !string.IsNullOrWhiteSpace(pilot)
        && _pilots.TryGetValue(pilot.Trim(), out var entry)
        && entry.CheckedIn;

    /// <summary>
    /// Files what the pilot was just told, so "say again" has something to repeat.
    ///
    /// Everything spoken to a pilot goes through here, not only tactical replies: the call a pilot
    /// most often misses is a long one, and the longest are the ATIS and the picture.
    /// </summary>
    public void Remember(string? pilot, string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return;
        Touch(pilot).LastReply = reply;
    }

    /// <summary>The last thing said to this pilot, or null if there is nothing on file.</summary>
    public string? LastReply(string? pilot) =>
        !string.IsNullOrWhiteSpace(pilot) && _pilots.TryGetValue(pilot.Trim(), out var entry)
            ? entry.LastReply
            : null;

    /// <summary>
    /// The reply to "say again": the last call repeated, or an honest negative.
    ///
    /// Deliberately not prefixed with "I say again" - the pilot asked to hear the call, and a
    /// preamble in front of a long ATIS is in the way. Repeating does not clear the slot, because
    /// a pilot who missed it twice will ask twice.
    /// </summary>
    public string SayAgain(string? pilot) => LastReply(pilot) ?? _config.DcsIntelNothingToRepeatReply;

    /// <summary>How many pilots are on file. For the suite and for the log line at shutdown.</summary>
    public int Count => _pilots.Count;

    /// <summary>Drops everything. Used when the bot reconnects to a different server.</summary>
    public void Clear() => _pilots.Clear();

    private Entry Touch(string? pilot)
    {
        var key = (pilot ?? "").Trim();
        if (key.Length == 0) key = "(unknown)";

        var entry = _pilots.GetOrAdd(key, _ => new Entry());
        entry.LastSeenUtc = DateTime.UtcNow;

        if (_pilots.Count > MaxPilots) Evict();
        return entry;
    }

    private void Evict()
    {
        // Taking a snapshot rather than ordering the live dictionary: another thread adding a
        // pilot mid-sort would throw, and losing a name is a cheaper failure than a crash in the
        // receive loop.
        foreach (var stale in _pilots.ToArray()
                     .OrderBy(p => p.Value.LastSeenUtc)
                     .Take(Math.Max(1, _pilots.Count - MaxPilots)))
        {
            _pilots.TryRemove(stale.Key, out _);
        }
    }

    private static string Fill(string? template, string? pilot)
    {
        var text = template ?? "";
        var name = string.IsNullOrWhiteSpace(pilot) ? "" : pilot.Trim();

        // With no name to use, the placeholder has to go along with the comma and space that
        // follow it, or the reply starts with ", radar contact".
        if (name.Length == 0)
            return text.Replace("{pilot}, ", "").Replace("{pilot}", "").TrimStart();

        return text.Replace("{pilot}", name);
    }
}
