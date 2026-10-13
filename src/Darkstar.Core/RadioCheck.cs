namespace Darkstar;

/// <summary>
/// "Overlord, radio check." — the most basic thing a radio does, and deliberately the least
/// entangled feature in the bot.
///
/// TWO DECISIONS WORTH KNOWING ABOUT:
///
/// It is not tied to a radio's role. A tower, an AWACS and a tanker all answer a radio check;
/// refusing one because that frequency is "only for airfield requests" would be absurd. So this
/// is checked before the per-radio gating rather than inside it.
///
/// And it works with no mission data at all. Without DCS-gRPC the bot can still confirm it hears
/// you, which is the whole point when you are trying to find out whether anything works. When
/// mission data IS available it adds whether the bot can see the pilot on scope - the quickest way
/// to discover that an SRS name and a DCS name don't line up, which is the failure that otherwise
/// silently turns every BRAA call into a bullseye call.
/// </summary>
public static class RadioCheck
{
    /// <summary>Whether this transmission is a radio check.</summary>
    public static bool Matches(AppConfig config, string? transcript) =>
        config.RadioCheckEnabled && TriggerMatcher.MatchesAny(transcript, config.RadioCheckTriggers);

    /// <summary>The trigger that fired, for the log.</summary>
    public static string? MatchedTrigger(AppConfig config, string? transcript) =>
        config.RadioCheckEnabled ? TriggerMatcher.FindMatch(transcript, config.RadioCheckTriggers) : null;

    /// <summary>What the bot knows about the pilot when answering.</summary>
    public enum ScopeState
    {
        /// <summary>No mission data to consult - say nothing about radar either way.</summary>
        Unknown,

        /// <summary>The pilot was matched to a unit in the running mission.</summary>
        Contact,

        /// <summary>Mission data was available but the pilot could not be matched to a unit.</summary>
        NoContact,
    }

    /// <summary>The spoken reply for a given scope state.</summary>
    public static string Reply(AppConfig config, ScopeState scope) => scope switch
    {
        ScopeState.Contact => config.RadioCheckReplyWithContact,
        ScopeState.NoContact => config.RadioCheckReplyNoContact,
        _ => config.RadioCheckReply,
    };
}
