using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The pilot read-back: the sentence, and which voice speaks it.
///
/// Both halves fail quietly. A wrong sentence is still a sentence, and two voices that happen to
/// sound alike still produce audio - neither looks broken in a log, and both only show up as
/// "the radio chatter is confusing" after a mission.
/// </summary>
internal static class PilotEchoTests
{
    public static void Run()
    {
        Section("The sentence the pilot's voice speaks");

        // Controller, then who is calling, then the request. That order lets everybody else on
        // the frequency stop listening after the first word.
        Check("controller, pilot, request",
            PilotEcho.Build("Overlord", "Punch 1-1", "bogey dope") == "Overlord, Punch 1-1, bogey dope.",
            PilotEcho.Build("Overlord", "Punch 1-1", "bogey dope"));

        Check("a full stop is added", PilotEcho.Build("Overlord", "Punch 1-1", "picture").EndsWith('.'));

        // "say again?." is a stumble in every engine.
        Check("but not doubled up",
            PilotEcho.Build("Overlord", "Punch 1-1", "say again?") == "Overlord, Punch 1-1, say again?",
            PilotEcho.Build("Overlord", "Punch 1-1", "say again?"));

        Section("A missing name leaves no gap in the sentence");

        Check("no pilot callsign",
            PilotEcho.Build("Overlord", "", "bogey dope") == "Overlord, bogey dope.",
            PilotEcho.Build("Overlord", "", "bogey dope"));
        Check("no controller callsign",
            PilotEcho.Build("", "Punch 1-1", "bogey dope") == "Punch 1-1, bogey dope.",
            PilotEcho.Build("", "Punch 1-1", "bogey dope"));
        Check("neither", PilotEcho.Build(null, null, "bogey dope") == "bogey dope.",
            PilotEcho.Build(null, null, "bogey dope"));

        // Nothing to say is said by saying nothing, not by transmitting a lone full stop.
        Check("no request at all is no sentence", PilotEcho.Build("Overlord", "Punch 1-1", "") == "");
        Check("nor is whitespace one", PilotEcho.Build("Overlord", "Punch 1-1", "   ") == "");
        Check("nor null", PilotEcho.Build("Overlord", "Punch 1-1", null) == "");

        Check("surrounding spaces are not spoken",
            PilotEcho.Build("  Overlord  ", "  Punch 1-1  ", "  bogey dope  ") == "Overlord, Punch 1-1, bogey dope.");

        Section("Every call has a phrase that can be read back");

        // The phrase has to be the request without the example callsigns, or the read-back says
        // "Overlord, Punch 1-1, Overlord, Punch one one checking in". A test rather than care,
        // because the two live on the same line and are edited together.
        var missing = CallCatalog.All.Where(c => string.IsNullOrWhiteSpace(c.EchoPhrase)).Select(c => c.Id).ToList();
        Check("no call is missing one", missing.Count == 0, string.Join(", ", missing));

        var drifted = CallCatalog.All
            .Where(c => !c.SpokenExample.EndsWith(c.EchoPhrase, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Id}: \"{c.SpokenExample}\" does not end with \"{c.EchoPhrase}\"")
            .ToList();
        Check("and each one is the tail of the spoken example", drifted.Count == 0, string.Join("; ", drifted));

        var withCallsign = CallCatalog.All
            .Where(c => c.EchoPhrase.Contains("Overlord", StringComparison.OrdinalIgnoreCase)
                        || c.EchoPhrase.Contains("Punch", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .ToList();
        Check("and carries no callsign of its own", withCallsign.Count == 0, string.Join(", ", withCallsign));

        // The read-back only happens for calls a pilot can click, so those are the ones that
        // have to come out as a sentence.
        foreach (var call in CallCatalog.All.Where(CallCatalog.MenuOffers))
        {
            var sentence = PilotEcho.Build("Overlord", "Punch 1-1", call.EchoPhrase);
            Check($"\"{call.Id}\" reads back as a sentence", sentence.StartsWith("Overlord, Punch 1-1, "), sentence);
        }

        Section("Picking a voice that is not the controller's");

        var male = new TtsVoice("Microsoft David Desktop", "en-US", "Male");
        var female = new TtsVoice("Microsoft Zira Desktop", "en-US", "Female");
        var male2 = new TtsVoice("Microsoft Mark", "en-US", "Male");
        var installed = new List<TtsVoice> { male, female, male2 };

        // A different gender is the thing a listener notices without thinking. Two male voices in
        // the same accent barely register as two people.
        //
        // The order here is what makes this test able to tell the two rules apart: the nearest
        // other voice is the second male one, so "first voice that is not the controller's" and
        // "first voice of another gender" give different answers. With the female voice in the
        // middle - as it is in the list above - both rules agree and the test proves nothing.
        var maleFirst = new List<TtsVoice> { male, male2, female };
        Check("a different gender wins over merely a different name",
            PilotEcho.PickVoice(maleFirst, "Microsoft David Desktop", "") == "Microsoft Zira Desktop",
            PilotEcho.PickVoice(maleFirst, "Microsoft David Desktop", ""));

        Check("the nearer same-gender voice is passed over for it",
            PilotEcho.PickVoice(maleFirst, "Microsoft David Desktop", "") != "Microsoft Mark");

        // A voice the tool reported without a gender cannot be judged on one, so it must not be
        // treated as "a different gender" - that would make the rule a coin toss.
        var unknownGender = new List<TtsVoice>
        {
            male, new("Mystery Voice", "en-US", ""), female,
        };
        Check("a voice with no gender reported is not mistaken for another gender",
            PilotEcho.PickVoice(unknownGender, "Microsoft David Desktop", "") == "Microsoft Zira Desktop",
            PilotEcho.PickVoice(unknownGender, "Microsoft David Desktop", ""));

        Check("and the other way round",
            PilotEcho.PickVoice(installed, "Microsoft Zira Desktop", "") == "Microsoft David Desktop",
            PilotEcho.PickVoice(installed, "Microsoft Zira Desktop", ""));

        // With nothing of another gender, a different name is still better than the same voice.
        var allMale = new List<TtsVoice> { male, male2 };
        Check("with one gender only, any other voice will do",
            PilotEcho.PickVoice(allMale, "Microsoft David Desktop", "") == "Microsoft Mark",
            PilotEcho.PickVoice(allMale, "Microsoft David Desktop", ""));

        // Never the controller's own voice: that is the one outcome the whole feature exists to
        // avoid, and it is also the one that looks like it worked.
        foreach (var controller in new[] { "Microsoft David Desktop", "Microsoft Zira Desktop", "Microsoft Mark" })
        {
            Check($"never the controller's own voice ({controller})",
                !string.Equals(PilotEcho.PickVoice(installed, controller, ""), controller, StringComparison.OrdinalIgnoreCase),
                PilotEcho.PickVoice(installed, controller, "") ?? "null");
        }

        Section("When there is nothing to pick");

        // One voice on the machine: both halves would be the same voice, which is worse than no
        // read-back at all because it sounds like the controller answering themselves.
        Check("a single installed voice gives none",
            PilotEcho.PickVoice(new List<TtsVoice> { male }, "Microsoft David Desktop", "") == null);
        Check("an empty list gives none", PilotEcho.PickVoice(new List<TtsVoice>(), "whatever", "") == null);
        Check("no list at all gives none", PilotEcho.PickVoice(null, "whatever", "") == null);

        // The radio has no voice configured, so the engine picks its own - which cannot be known
        // here. The first entry is the engine's own first choice, so the second is the safer bet.
        Check("with no controller voice, the second entry is taken",
            PilotEcho.PickVoice(installed, "", "") == "Microsoft Zira Desktop",
            PilotEcho.PickVoice(installed, "", ""));
        Check("and with only one voice, none",
            PilotEcho.PickVoice(new List<TtsVoice> { male }, "", "") == null);

        Section("A configured voice is honoured as given");

        Check("a configured name is used",
            PilotEcho.PickVoice(installed, "Microsoft David Desktop", "Microsoft Mark") == "Microsoft Mark");

        // Including deliberately the same voice on both sides: somebody who typed that meant it.
        Check("even when it matches the controller",
            PilotEcho.PickVoice(installed, "Microsoft David Desktop", "Microsoft David Desktop")
                == "Microsoft David Desktop");

        // The list comes from a tool that may have failed to run. Refusing a working name over a
        // failed lookup would be the worse mistake.
        Check("even when the list is empty",
            PilotEcho.PickVoice(new List<TtsVoice>(), "Overlord voice", "Microsoft Mark") == "Microsoft Mark");

        Check("surrounding spaces are not part of the name",
            PilotEcho.PickVoice(installed, "Microsoft David Desktop", "  Microsoft Mark  ") == "Microsoft Mark");

        Section("The same machine picks the same voice at every start");

        // A voice that changes between restarts is a voice pilots cannot learn.
        var first = PilotEcho.PickVoice(installed, "Microsoft David Desktop", "");
        for (var i = 0; i < 5; i++)
            Check($"run {i + 1} agrees", PilotEcho.PickVoice(installed, "Microsoft David Desktop", "") == first);

        Section("The hint when no voice could be found");

        var hint = PilotEcho.NoVoiceHint("Microsoft David Desktop");
        Check("it names the voice already in use", hint.Contains("Microsoft David Desktop"), hint);
        Check("it says what the consequence is", hint.Contains("off", StringComparison.OrdinalIgnoreCase), hint);
        Check("and what fixes it", hint.Contains("Installing another", StringComparison.OrdinalIgnoreCase), hint);
        Check("it survives having no voice to name",
            !PilotEcho.NoVoiceHint("").Contains("besides"), PilotEcho.NoVoiceHint(""));
    }
}
