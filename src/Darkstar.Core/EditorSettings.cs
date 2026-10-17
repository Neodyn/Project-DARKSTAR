using System.Text.Json;

namespace Darkstar;

/// <summary>
/// The few things the config editor has to remember about itself between launches: which folder
/// the bot's <c>config.json</c> is in, and which profile was last activated.
///
/// Deliberately not in <c>config.json</c>. That file belongs to the bot, is copied around by
/// <see cref="ConfigProfiles"/>, and would carry the editor's own state into every profile - so
/// activating a profile could change where the editor looks, which is a loop nobody wants to
/// debug. This lives beside neither the bot nor the profiles, in the user's own app-data folder,
/// where it is also writable when the bot is installed under Program Files.
///
/// Until now the editor found the config folder by searching upward from its own location on every
/// start (see <c>ConfigStore.ResolveDefaultConfigFolder</c>). That guess is right on a normal
/// installation and wrong exactly when it matters - two copies of config.json on the machine, which
/// is the case this whole area exists to make visible. A folder the operator has pointed at by hand
/// is an answer, and it should not have to be given again at the next launch.
/// </summary>
public sealed class EditorSettings
{
    /// <summary>
    /// Folder holding the bot's config.json, as chosen by the operator. Null means nothing was
    /// ever chosen, and the editor goes back to searching for one.
    /// </summary>
    public string? ConfigFolder { get; set; }

    /// <summary>
    /// The profile activated last, so the editor can show which one is in effect. Only a label:
    /// what the bot reads is config.json, and <see cref="ConfigProfiles.MatchesLiveConfig"/> is
    /// what decides whether this name is still true.
    /// </summary>
    public string? ActiveProfile { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Where this file lives: <c>%LOCALAPPDATA%\Darkstar\editor.json</c> on Windows, the
    /// equivalent elsewhere.
    /// </summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Darkstar",
            "editor.json");

    /// <summary>
    /// Reads the settings, or returns empty ones. Never throws: a corrupt or unreadable file means
    /// the editor starts the way it did before this existed, which is a working editor.
    /// </summary>
    public static EditorSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new EditorSettings();

            return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path), JsonOptions)
                   ?? new EditorSettings();
        }
        catch (Exception)
        {
            return new EditorSettings();
        }
    }

    /// <summary>
    /// Writes the settings, creating the folder. Returns whether it worked rather than throwing -
    /// the editor must stay usable on a machine where this cannot be written, it just will not
    /// remember anything.
    /// </summary>
    public bool Save(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
