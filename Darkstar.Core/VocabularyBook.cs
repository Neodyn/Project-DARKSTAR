using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkstar;

/// <summary>
/// Loads/creates vocabulary.json: a plain list of terms (aircraft types, callsigns, unit names,
/// or any other words the speech recognition tends to mishear) that get passed to Gemini as a
/// hint alongside the audio. This doesn't change what the bot can talk about (that's still
/// phrases.json / the Gemini prompt) - it purely helps transcription accuracy for terms that
/// aren't common English words, e.g. "Viggen", "Overlord", "Bullseye".
/// </summary>
public static class VocabularyBook
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Writes the term list back to path, backing up the previous version first.</summary>
    public static void Save(string path, List<string> terms)
    {
        BackupUtils.BackupBeforeWrite(path);
        var json = JsonSerializer.Serialize(terms, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static List<string> LoadOrCreateDefault(string path)
    {
        if (!File.Exists(path))
        {
            // Proper nouns and jargon that generic speech recognition gets wrong - callsigns,
            // aircraft types, map names.
            //
            // NOT the trigger phrases from config.json. The transcriber is told to snap anything
            // that merely SOUNDS like one of these terms onto its exact spelling, which is what
            // makes the hints work - and it does that to unintelligible audio too. A command
            // phrase in this list therefore turns every mumble into that command: "Bogey Dope"
            // used to be a default here, and the result was the bot answering bogey dope whenever
            // it couldn't make out what was said. AppConfig.WarnAboutVocabularyTriggerConflicts now warns about it.
            var defaults = new List<string>
            {
                "Overlord",
                "Enfield",
                "Springfield",
                "Batumi",
                "RTB",
            };

            var json = JsonSerializer.Serialize(defaults, JsonOptions);
            File.WriteAllText(path, json);

            Logger.Log($"No vocabulary.json was found. A new file with sample terms was created: {Path.GetFullPath(path)}");
            Logger.Log("Add aircraft types, callsigns, or other terms there that the speech recognition should transcribe correctly.");
            return defaults;
        }

        try
        {
            var text = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<string>>(text, JsonOptions) ?? new List<string>();
            entries = entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
            return entries;
        }
        catch (JsonException ex)
        {
            Logger.Log($"WARNING: vocabulary.json has a JSON syntax error and could not be parsed ({ex.Message}) - no vocabulary hints will be used.");
            return new List<string>();
        }
        catch (Exception ex)
        {
            Logger.Log($"WARNING: Could not read vocabulary.json ({ex.Message}) - no vocabulary hints will be used.");
            return new List<string>();
        }
    }
}
