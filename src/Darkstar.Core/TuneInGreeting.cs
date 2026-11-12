namespace Darkstar;

/// <summary>
/// Greeting a pilot who has just tuned onto one of the bot's frequencies, with the callsign of the
/// channel they reached and where to find the tactical radio.
///
/// WHY THIS IS WORTH DOING: the generated tower frequencies are invented (see <see cref="TowerPlan"/>)
/// and the pilots learn them from an F10 marker they may never open. A pilot who dials one in and
/// hears nothing cannot tell a working bot from a broken one - and the thing they most need next is
/// where the tactical radio is, which no marker at their departure field will tell them once they
/// are airborne.
///
/// WHY IT IS THROTTLED HARD: SRS has no unicast. Every word goes to EVERYONE on the frequency. Ten
/// pilots tuning in over ten minutes means ten greetings on a channel other people are trying to use,
/// and a pilot flipping back and forth between two presets would trigger one each time. So:
///
///   * once per client per frequency, for the whole session - never again, even hours later
///   * a minimum gap between greetings on the same frequency, so a flight checking in together
///     hears one and not four
///   * never while that radio is busy, which the caller enforces through the existing transmit lock
///
/// The failure this is built to avoid is a bot that talks over its own users while trying to be
/// helpful. When in doubt it stays silent: a missed greeting costs a pilot one question, a greeting
/// at the wrong moment costs somebody else their BRAA call.
/// </summary>
public static class TuneInGreeting
{
    /// <summary>One client's radio state, as SRS reports it.</summary>
    /// <param name="ClientGuid">SRS's own id for the client - stable for as long as it is connected.</param>
    /// <param name="Name">The SRS display name, for the log and for addressing the pilot.</param>
    /// <param name="Coalition">0 = spectator/unknown, 1 = red, 2 = blue.</param>
    /// <param name="Frequencies">Every frequency the client has tuned on an ENABLED radio, in Hz.</param>
    public sealed record ClientRadios(string ClientGuid, string Name, int Coalition, IReadOnlyList<double> Frequencies);

    /// <summary>
    /// SRS's modulation values. 3 means the radio slot is switched off, which is how a radio the
    /// pilot has not powered up yet is told apart from one tuned to a frequency.
    /// </summary>
    public const int ModulationDisabled = 3;

    /// <summary>Whether a radio slot counts as tuned at all.</summary>
    public static bool IsTuned(double frequencyHz, int modulation) =>
        modulation != ModulationDisabled && frequencyHz > 1_000_000;

    /// <summary>
    /// Remembers who has already been greeted on what, and enforces the gap between greetings.
    /// One instance per bot run; not shared between radios, because the gap is per frequency.
    /// </summary>
    public sealed class State
    {
        private readonly HashSet<string> _greeted = new(StringComparer.Ordinal);
        private readonly Dictionary<long, DateTime> _lastGreetingPerFrequency = new();

        /// <summary>
        /// How many pilot/frequency pairs are remembered before the greeting gives up and stays
        /// quiet. Unreachable on any real server - it is there so a session running for weeks
        /// cannot grow this set without bound, and the quiet is deliberate: forgetting somebody
        /// in order to make room would greet them a second time, which is the one outcome this
        /// whole class exists to prevent.
        /// </summary>
        public const int MaxRemembered = 500;

        /// <summary>How many greetings have been sent, for the log.</summary>
        public int Sent { get; private set; }

        /// <summary>
        /// Whether to greet this client on this frequency now.
        /// </summary>
        /// <param name="pilotName">
        /// The SRS player name. This, not the client id, is what makes the memory last: SRS issues
        /// a NEW ClientGuid every time a client reconnects, and in DCS that happens whenever a
        /// pilot changes slot, respawns or their SRS client blips - so a guid-keyed memory greeted
        /// the same person again and again during one mission, which is exactly what this class
        /// documents that it does not do.
        /// </param>
        /// <param name="separator">
        /// <c>PlayerNameCallsignSeparator</c>. With the "CALLSIGN 1-1 | handle" convention the
        /// handle is the stable half - a pilot moving from Enfield 1-1 to Springfield 2-1 is the
        /// same human and must not be greeted twice.
        /// </param>
        /// <param name="minimumGap">
        /// Minimum time between greetings on the same frequency. A flight of four checking in
        /// together must hear one greeting, not four.
        /// </param>
        public bool ShouldGreet(string clientGuid, string? pilotName, string? separator,
            double frequencyHz, DateTime now, TimeSpan minimumGap)
        {
            var who = Identify(pilotName, separator, clientGuid);
            if (who.Length == 0) return false;

            var frequencyKey = (long)Math.Round(frequencyHz);
            var key = $"{who}@{frequencyKey}";

            // Checked before the gap on purpose: a client that was already greeted must never reset
            // the gap for everybody else by tuning in again.
            if (_greeted.Contains(key)) return false;

            if (_greeted.Count >= MaxRemembered) return false;

            if (_lastGreetingPerFrequency.TryGetValue(frequencyKey, out var last) && now - last < minimumGap)
                return false;

            _greeted.Add(key);
            _lastGreetingPerFrequency[frequencyKey] = now;
            Sent++;
            return true;
        }

        /// <summary>
        /// Forgets a client that has disconnected, so somebody who rejoins later is greeted again.
        /// Their guid changes on reconnect anyway; this is about not growing forever on a server
        /// that runs for weeks.
        /// </summary>
        /// <summary>How many pilot/frequency pairs are being remembered, for the log.</summary>
        public int Tracked => _greeted.Count;
    }

    /// <summary>
    /// Who this is, for the purpose of "have we greeted them already" - stable across the
    /// reconnects SRS reports during one mission.
    ///
    /// The handle wins when the "CALLSIGN 1-1 | handle" convention is in use, because that is the
    /// half that survives a slot change. Without a separator the whole name is the identity, which
    /// is also stable - a player's SRS name does not change when they re-slot. The client id is
    /// the last resort and the reason the old behaviour was wrong: it changes on every reconnect.
    ///
    /// Returns "" when there is nothing to key on at all, which the caller treats as "do not
    /// greet" - a greeting nobody can be credited with is a greeting that repeats.
    /// </summary>
    internal static string Identify(string? pilotName, string? separator, string? clientGuid)
    {
        var parts = PilotNames.Split(pilotName, separator);

        var handle = PilotNames.CanonicalKey(parts.Handle);
        if (handle.Length > 0) return "h:" + handle;

        var callsign = PilotNames.CanonicalKey(parts.Callsign);
        if (callsign.Length > 0) return "n:" + callsign;

        var guid = (clientGuid ?? "").Trim();
        return guid.Length > 0 ? "g:" + guid : "";
    }

    /// <summary>
    /// "Punch 1-1, Batumi Tower. Overlord is on two five one decimal zero."
    /// </summary>
    /// <param name="pilotName">The SRS name, or empty - then the greeting is not addressed to anybody.</param>
    /// <param name="channelCallsign">The callsign of the radio the pilot tuned onto.</param>
    /// <param name="tacticalRadios">
    /// The radios that answer tactical requests - "where Overlord is". Empty is normal on a server
    /// with no DCS-gRPC, and then the greeting simply does not mention them.
    /// </param>
    public static string Build(string? pilotName, string channelCallsign,
        IEnumerable<(double FrequencyHz, string Callsign)> tacticalRadios, AppConfig config)
    {
        var template = config.TuneInGreetingText;
        if (string.IsNullOrWhiteSpace(template)) return "";

        var tactical = tacticalRadios
            .Where(r => r.FrequencyHz > 1_000_000)
            .OrderBy(r => r.FrequencyHz)
            .ToList();

        // Never names the channel the pilot is already on: "Batumi Tower is on 133.000" to somebody
        // listening on 133.000 is noise.
        var elsewhere = tactical
            .Where(r => !string.Equals(r.Callsign, channelCallsign, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{PilotNames.ForSpeech(r.Callsign)} is on " +
                         RadioRoles.SpeakFrequency(r.FrequencyHz, config.DcsIntelSlowSpeech))
            .ToList();

        var reply = template
            .Replace("{pilot}", PilotNames.ForSpeech(pilotName))
            .Replace("{callsign}", PilotNames.ForSpeech(channelCallsign))
            .Replace("{tactical}", elsewhere.Count == 0 ? "" : string.Join(", ", elsewhere) + ".");

        // An unaddressed greeting must not start with a comma, and a server without tactical radios
        // must not leave a dangling sentence.
        return Tidy(reply);
    }

    /// <summary>
    /// Removes the gaps an empty placeholder leaves behind. Done on the finished sentence rather
    /// than by branching per template, because the template is the operator's to rewrite.
    /// </summary>
    internal static string Tidy(string text)
    {
        var tidied = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        tidied = tidied.TrimStart(',', '.', ' ');
        tidied = System.Text.RegularExpressions.Regex.Replace(tidied, @"\s+([,.])", "$1");
        tidied = System.Text.RegularExpressions.Regex.Replace(tidied, @",\s*\.", ".");
        tidied = System.Text.RegularExpressions.Regex.Replace(tidied, @"\.\s*\.", ".");

        return tidied.Trim();
    }
}
