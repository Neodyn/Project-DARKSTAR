namespace Darkstar;

/// <summary>
/// The pilot's own call, spoken on the frequency before the controller answers.
///
/// A spoken request is already audible to everybody on the frequency - the pilot transmitted it.
/// An F10 menu click is not: the bot answers a question nobody heard, and the other pilots get
/// half a conversation. On a shared frequency that is worse than silence, because an answer with
/// no question reads as a call to somebody else.
///
/// So a menu click is read back in a second voice first:
///
///   Punch 1-1 (pilot voice):  "Overlord, Punch 1-1, bogey dope."
///   Overlord  (controller):   "Punch 1-1, this is Overlord. Bogey, bearing zero, niner, zero..."
///
/// Both halves of this are decisions that can be wrong in ways nobody notices until a mission is
/// running - the wording, and whether the two voices are actually distinguishable - so both are
/// pure functions here rather than inline in the transmit path.
/// </summary>
public static class PilotEcho
{
    /// <summary>
    /// The sentence the pilot's voice speaks: controller first, then who is calling, then the
    /// request. That is the order a pilot actually uses, and the order that lets everybody else
    /// on the frequency stop listening after the first word if it is not for them.
    /// </summary>
    /// <param name="controllerCallsign">The radio's callsign, e.g. "Overlord". Left out when empty.</param>
    /// <param name="pilotCallsign">Who is calling, e.g. "Punch 1-1". Left out when empty.</param>
    /// <param name="callPhrase">The request itself, e.g. "bogey dope".</param>
    /// <returns>The sentence, or an empty string when there is no request to speak.</returns>
    public static string Build(string? controllerCallsign, string? pilotCallsign, string? callPhrase)
    {
        var phrase = (callPhrase ?? "").Trim();
        if (phrase.Length == 0) return "";

        var parts = new List<string>();

        var controller = (controllerCallsign ?? "").Trim();
        if (controller.Length > 0) parts.Add(controller);

        var pilot = (pilotCallsign ?? "").Trim();
        if (pilot.Length > 0) parts.Add(pilot);

        parts.Add(phrase);

        var sentence = string.Join(", ", parts);

        // A phrase that already ends in punctuation keeps it: "say again?" must not become
        // "say again?." - every engine reads that as a stumble.
        return sentence.EndsWith('.') || sentence.EndsWith('?') || sentence.EndsWith('!')
            ? sentence
            : sentence + ".";
    }

    /// <summary>
    /// Which voice speaks the pilot's half.
    ///
    /// The point of the second voice is that a listener can tell the two sides apart without
    /// thinking about it. Two male voices in the same accent barely manage that, so a voice of a
    /// different gender is preferred over merely a different name - and a different name is in
    /// turn preferred over none.
    ///
    /// Null means this machine has nothing to offer: the caller says so once at startup rather
    /// than transmitting both halves in one voice and leaving somebody to work out why the
    /// readback sounds like the controller talking to themselves.
    /// </summary>
    /// <param name="installed">The voices this machine has, in the order the tool printed them.</param>
    /// <param name="controllerVoice">The radio's own voice. Empty means the engine's default, which is not knowable here.</param>
    /// <param name="configured">An explicit choice from the config. Honoured as given, including a deliberate match.</param>
    public static string? PickVoice(IReadOnlyList<TtsVoice>? installed, string? controllerVoice, string? configured)
    {
        // An administrator who typed a name meant it - including, deliberately, the same voice on
        // both sides. Not second-guessed, and not checked against the list either: the list comes
        // from a tool that may have failed to run, and refusing a working name over a failed
        // lookup would be the worse mistake.
        var chosen = (configured ?? "").Trim();
        if (chosen.Length > 0) return chosen;

        if (installed == null || installed.Count == 0) return null;

        var controller = (controllerVoice ?? "").Trim();

        // With no configured controller voice, the engine picks its own default and this cannot
        // know which one that is. The first voice in the list is the engine's own first choice,
        // so the second one is the safest bet at being a different voice.
        if (controller.Length == 0)
            return installed.Count > 1 ? installed[1].Name : null;

        var controllerGender = installed
            .FirstOrDefault(v => string.Equals(v.Name, controller, StringComparison.OrdinalIgnoreCase))
            ?.Gender ?? "";

        var different = installed
            .Where(v => !string.Equals(v.Name, controller, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (different.Count == 0) return null;

        // Order is the order the tool printed, so the same machine picks the same voice at every
        // start. A voice that changes between restarts is a voice pilots cannot learn.
        if (controllerGender.Length > 0)
        {
            var otherGender = different.FirstOrDefault(v =>
                v.Gender.Length > 0 && !string.Equals(v.Gender, controllerGender, StringComparison.OrdinalIgnoreCase));

            if (otherGender != null) return otherGender.Name;
        }

        return different[0].Name;
    }

    /// <summary>
    /// What to log when <see cref="PickVoice"/> found nothing. Says what the consequence is and
    /// what fixes it, because "no echo voice" on its own tells a server owner nothing.
    /// </summary>
    public static string NoVoiceHint(string? controllerVoice)
    {
        var controller = (controllerVoice ?? "").Trim();
        var which = controller.Length > 0 ? $" besides \"{controller}\"" : "";

        return $"[Menu] No second voice is installed{which}, so the pilot read-back is switched off " +
               "for this radio - both halves in one voice would be worse than none. " +
               "Installing another SAPI5 voice turns it on (CH5 Speech lists where they come from).";
    }
}
