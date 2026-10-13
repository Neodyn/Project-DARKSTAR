namespace Darkstar;

/// <summary>
/// Tidying what Whisper emits, kept apart from the client that calls it.
///
/// Same split as <see cref="VoskModelCheck"/> against the detector: the part with no dependency
/// lives in Core, where the test suite can exercise the shipped code instead of a copy of it.
/// </summary>
public static class WhisperTranscript
{
    /// <summary>
    /// Turns what Whisper emits into what a pilot actually said.
    ///
    /// Whisper pads segments with a leading space and writes bracketed markers - [BLANK_AUDIO],
    /// (wind blowing), (radio chatter) - for anything it cannot turn into words. Those are not
    /// things anybody said, and left in they reach the trigger matcher: "[BLANK_AUDIO]" is
    /// harmless, but "(radio chatter)" is one unlucky word away from matching a trigger phrase and
    /// answering a request nobody made. That failure already happened once in this project, with
    /// "bogey dope" in the default vocabulary, and it took a while to find.
    ///
    /// Pure, and in Core rather than beside the client, so the suite exercises THIS code rather
    /// than a copy of the expression - a test that reimplements what it checks passes happily
    /// while the shipped version is broken, which is exactly what happened when it was first written.
    /// </summary>
    public static string Clean(string raw)
    {
        var withoutMarkers = System.Text.RegularExpressions.Regex
            .Replace(raw, @"[\[\(][^\]\)]*[\]\)]", " ");

        return System.Text.RegularExpressions.Regex
            .Replace(withoutMarkers.Trim(), @"\s{2,}", " ");
    }
}
