using System.Collections.Concurrent;

namespace Darkstar;

/// <summary>
/// Stops one pilot from using up the frequency and the Gemini quota by themselves.
///
/// WHAT GOES WRONG WITHOUT IT: every transmission costs a Gemini call, and every reply occupies the
/// frequency for several seconds while nobody else can be heard. One pilot repeating the wake word -
/// out of boredom, out of frustration that the bot misheard them, or because a stuck transmit key is
/// feeding it their cockpit noise - is therefore enough to exhaust a free-tier quota for everybody
/// and to keep the channel busy at the same time. Neither failure looks like the pilot who caused it.
///
/// WHY A SLIDING WINDOW AND NOT A COOLDOWN: a fixed pause after each request punishes the normal
/// case, where a pilot asks two or three things in quick succession and then flies for ten minutes.
/// Counting requests inside a moving window lets that burst through and only steps in on a pilot who
/// keeps going.
///
/// WHAT IT DELIBERATELY DOES NOT DO: it never silently drops a transmission. A pilot who is over the
/// limit is told so, once per limited stretch, because a bot that simply stops answering is
/// indistinguishable from a broken one - and the pilot's next move would be to transmit more.
/// </summary>
public sealed class RateLimiter
{
    /// <summary>What to do with a transmission.</summary>
    public enum Verdict
    {
        /// <summary>Under the limit. Answer normally.</summary>
        Allow,

        /// <summary>Over the limit, and this is the first one - say so.</summary>
        RejectAndSay,

        /// <summary>Over the limit and already told. Stay silent rather than nagging.</summary>
        RejectSilently
    }

    private sealed class PilotHistory
    {
        /// <summary>When each counted request arrived, oldest first.</summary>
        public readonly Queue<DateTime> Requests = new();

        /// <summary>Whether this pilot has already been told they are over the limit.</summary>
        public bool WasWarned;
    }

    private readonly ConcurrentDictionary<string, PilotHistory> _histories = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxRequests;
    private readonly TimeSpan _window;

    /// <param name="maxRequests">Requests allowed inside the window. Zero or less disables the limit.</param>
    /// <param name="windowSeconds">Length of the sliding window.</param>
    public RateLimiter(int maxRequests, double windowSeconds)
    {
        _maxRequests = maxRequests;
        _window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds));
    }

    /// <summary>Whether this limiter does anything at all.</summary>
    public bool Enabled => _maxRequests > 0;

    /// <summary>How many pilots are currently being tracked, for the log.</summary>
    public int TrackedPilots => _histories.Count;

    /// <summary>
    /// Records a request and says what to do with it.
    /// </summary>
    /// <param name="pilot">
    /// The pilot's SRS name. Used as given rather than normalised: the aim is to limit one client,
    /// and the SRS name is what identifies one. An empty name is not limited - it would lump every
    /// unidentified transmission into one bucket and starve them all.
    /// </param>
    /// <param name="now">The current time, injected so the window can be tested without waiting.</param>
    public Verdict Check(string? pilot, DateTime now)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(pilot)) return Verdict.Allow;

        var history = _histories.GetOrAdd(pilot.Trim(), _ => new PilotHistory());

        // Per pilot rather than one lock for everything: two pilots on different frequencies are
        // handled on different threads and have nothing to say to each other.
        lock (history)
        {
            var cutoff = now - _window;
            while (history.Requests.Count > 0 && history.Requests.Peek() < cutoff)
                history.Requests.Dequeue();

            if (history.Requests.Count >= _maxRequests)
            {
                // Rejected requests are NOT recorded. Counting them would extend the window every
                // time the pilot tried again, so somebody who keeps calling could never get back in -
                // a limit that turns into a ban is not what was configured.
                var alreadyWarned = history.WasWarned;
                history.WasWarned = true;
                return alreadyWarned ? Verdict.RejectSilently : Verdict.RejectAndSay;
            }

            history.Requests.Enqueue(now);

            // Back under the limit, so the next time they go over they are told again rather than
            // being silently ignored because of something that happened half an hour ago.
            history.WasWarned = false;
            return Verdict.Allow;
        }
    }

    /// <summary>
    /// How long until this pilot's oldest counted request falls out of the window - the wait before
    /// they are heard again. Zero when they are not limited.
    /// </summary>
    public TimeSpan RetryAfter(string? pilot, DateTime now)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(pilot)) return TimeSpan.Zero;
        if (!_histories.TryGetValue(pilot.Trim(), out var history)) return TimeSpan.Zero;

        lock (history)
        {
            if (history.Requests.Count < _maxRequests) return TimeSpan.Zero;

            var wait = history.Requests.Peek() + _window - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Drops pilots who haven't transmitted for a while, so a long-running server doesn't keep a
    /// record per name that ever connected. Called from the watchdog loop, not per request.
    /// </summary>
    /// <returns>How many were forgotten.</returns>
    public int Prune(DateTime now)
    {
        var cutoff = now - _window;
        var removed = 0;

        foreach (var (name, history) in _histories.ToArray())
        {
            bool empty;
            lock (history)
            {
                while (history.Requests.Count > 0 && history.Requests.Peek() < cutoff)
                    history.Requests.Dequeue();

                empty = history.Requests.Count == 0;
            }

            if (empty && _histories.TryRemove(name, out _)) removed++;
        }

        return removed;
    }

    /// <summary>
    /// The spoken refusal. <c>{pilot}</c> and <c>{seconds}</c> are filled in; the wait is rounded up,
    /// because being told to wait five seconds and then refused again at 4.6 reads as a broken bot.
    /// </summary>
    public static string BuildReply(string template, string? pilot, TimeSpan retryAfter)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        return template
            .Replace("{pilot}", PilotNames.ForSpeech(pilot))
            .Replace("{seconds}", seconds.ToString());
    }
}
