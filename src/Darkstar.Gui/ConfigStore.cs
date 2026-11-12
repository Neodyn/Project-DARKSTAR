using System.IO;
using System.Linq;

namespace Darkstar.Gui;

/// <summary>
/// Single shared instance (registered as a DI singleton, see App.xaml.cs) that all Razor
/// components read from and write to. Wraps the exact same load/save/validate logic the bot
/// service itself uses (Darkstar.Core: AppConfig, PhraseBook, VocabularyBook) - the GUI never
/// re-implements parsing or validation, it just edits the same objects and writes them back.
/// </summary>
public sealed class ConfigStore
{
    /// <summary>
    /// What the editor remembers between launches: the chosen config folder and the last
    /// activated profile. Loaded once, written whenever one of them changes.
    /// </summary>
    private readonly EditorSettings _settings = EditorSettings.Load(EditorSettings.DefaultPath());

    /// <summary>
    /// Folder containing config.json/phrases.json/vocabulary.json. A folder the operator has
    /// chosen before wins; otherwise it is found by searching near the editor's own location
    /// (see ResolveDefaultConfigFolder), so the GUI and the bot service see the same files out of
    /// the box with no manual setup. Change it on CH3 Profiles if the bot lives elsewhere.
    ///
    /// Settable directly for the "not loaded" screen's text box; UseFolder is what remembers it.
    /// </summary>
    public string ConfigFolder { get; set; } = "";

    /// <summary>
    /// Points the editor at another folder, remembers it for the next launch, and loads from it.
    ///
    /// Remembering matters more than it looks: the search below is right on a normal installation
    /// and wrong exactly when there are two copies of config.json on the machine, which is the
    /// situation that sends somebody to the editor in the first place.
    /// </summary>
    public void UseFolder(string folder)
    {
        var trimmed = (folder ?? "").Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            SetStatus("Give a folder first.", isError: true);
            NotifyStateChanged();
            return;
        }

        if (!Directory.Exists(trimmed))
        {
            // Not created here on purpose. A typo in a path would otherwise produce an empty
            // folder with a default config.json in it, and the editor would look like it had
            // worked while the bot went on reading the folder it always did.
            SetStatus($"There is no folder {trimmed}.", isError: true);
            NotifyStateChanged();
            return;
        }

        ConfigFolder = trimmed;
        Remember(s => s.ConfigFolder = trimmed);
        Load();
    }

    /// <summary>Whether the folder in use is one somebody chose, rather than one that was found.</summary>
    public bool FolderWasChosen =>
        !string.IsNullOrWhiteSpace(_settings.ConfigFolder)
        && string.Equals(_settings.ConfigFolder, ConfigFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Forgets the chosen folder, so the next launch searches for one again.</summary>
    public void ForgetChosenFolder()
    {
        Remember(s => s.ConfigFolder = null);
        SetStatus("The folder is no longer remembered - the editor will search for one on the next start.", isError: false);
        NotifyStateChanged();
    }

    private void Remember(Action<EditorSettings> change)
    {
        change(_settings);

        if (!_settings.Save(EditorSettings.DefaultPath()))
            SetStatus("Could not write the editor's own settings - this choice will not survive a restart.", isError: true);
    }

    /// <summary>
    /// The GUI (Darkstar.Gui) and the bot service (Darkstar) are separate executables with
    /// separate build output folders, so AppContext.BaseDirectory differs between them - most
    /// noticeably during development in Visual Studio, where the GUI's own .exe sits several
    /// folders deep (Darkstar.Gui\bin\Debug\net8.0-windows\...) while the bot's own config.json
    /// ends up under its own build output (e.g. Darkstar\bin\Debug\net8.0\, or directly under
    /// bin\Debug\net8.0\ if Darkstar.csproj sits at the solution root) - a sibling branch of the
    /// folder tree, not a direct ancestor of the GUI's own path. To find the same file both
    /// projects use without any manual configuration: walk a few levels up from the GUI's own
    /// folder, and at each level check both that folder directly AND recursively search any
    /// "bin" subfolder there (which covers other projects' build output living alongside it).
    /// </summary>
    private static string ResolveDefaultConfigFolder()
    {
        var here = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(here, "config.json")))
            return here;

        var dir = new DirectoryInfo(here).Parent;
        for (var i = 0; i < 6 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config.json")))
                return dir.FullName;

            // Also check any "bin" subfolder at this level (e.g. C:\BOT_SRS\bin\Debug\net8.0\)
            // for another project's build output containing config.json - this is where the
            // bot service's own config.json typically ends up during development.
            var binDir = Path.Combine(dir.FullName, "bin");
            if (Directory.Exists(binDir))
            {
                var found = Directory.EnumerateFiles(binDir, "config.json", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null)
                    return Path.GetDirectoryName(found)!;
            }

            dir = dir.Parent;
        }

        // Nothing found anywhere nearby - fall back to the GUI's own folder. A fresh
        // config.json will be created there on first Load(), same as the bot would do.
        return here;
    }

    public ConfigStore()
    {
        // A folder somebody chose beats one that was found, and it is only honoured while it is
        // still there - a remembered path to a drive that is no longer plugged in should fall
        // back to the search rather than leave the editor pointing at nothing.
        ConfigFolder = !string.IsNullOrWhiteSpace(_settings.ConfigFolder)
                       && Directory.Exists(_settings.ConfigFolder)
            ? _settings.ConfigFolder
            : ResolveDefaultConfigFolder();
    }

    public AppConfig Config { get; private set; } = new();
    public List<PhraseEntry> Phrases { get; private set; } = new();
    public List<string> Vocabulary { get; private set; } = new();

    public bool IsLoaded { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool StatusIsError { get; private set; }

    /// <summary>Raised whenever data changes, so components can call StateHasChanged().</summary>
    public event Action? OnChange;
    private void NotifyStateChanged() => OnChange?.Invoke();

    private string ConfigPath => Path.Combine(ConfigFolder, "config.json");
    private string PhrasesPath => Path.Combine(ConfigFolder, "phrases.json");
    private string VocabularyPath => Path.Combine(ConfigFolder, "vocabulary.json");

    /// <summary>
    /// Loads all three files from ConfigFolder. Uses the bot's own LoadOrCreateDefault, so
    /// missing files get created with sane defaults (matching exactly what happens when the bot
    /// itself starts for the first time) and any warnings get written to logs/ under ConfigFolder.
    /// </summary>
    public void Load()
    {
        try
        {
            Logger.Init(Path.Combine(ConfigFolder, "logs"));

            var configExisted = File.Exists(ConfigPath);
            var loadedConfig = AppConfig.LoadOrCreateDefault(ConfigPath);
            if (loadedConfig == null && !configExisted)
            {
                // The first call just created a brand-new default file (the bot service treats
                // that as "please review and restart") - load it straight back so the user can
                // review/edit it immediately in the GUI instead.
                loadedConfig = AppConfig.LoadOrCreateDefault(ConfigPath);
            }

            if (loadedConfig == null)
            {
                // The file existed but could not be parsed (invalid JSON). Do NOT silently fall
                // back to a blank config here - that would risk overwriting the user's real
                // settings with defaults the next time they hit Save.
                IsLoaded = false;
                SetStatus("config.json exists but has invalid JSON - fix the syntax (see the log file) before using this editor.", isError: true);
                NotifyStateChanged();
                return;
            }

            Config = loadedConfig;
            Phrases = PhraseBook.LoadOrCreateDefault(PhrasesPath);
            Vocabulary = VocabularyBook.LoadOrCreateDefault(VocabularyPath);

            IsLoaded = true;
            SetStatus($"Loaded from {ConfigFolder}", isError: false);
        }
        catch (Exception ex)
        {
            IsLoaded = false;
            SetStatus($"Failed to load: {ex.Message}", isError: true);
        }

        NotifyStateChanged();
    }

    public void SaveConfig()
    {
        try
        {
            Config.Save(ConfigPath);
            SetStatus("config.json saved.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save config.json: {ex.Message}", isError: true);
        }
        NotifyStateChanged();
    }

    public void SavePhrases()
    {
        try
        {
            PhraseBook.Save(PhrasesPath, Phrases);
            SetStatus("phrases.json saved.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save phrases.json: {ex.Message}", isError: true);
        }
        NotifyStateChanged();
    }

    public void SaveVocabulary()
    {
        try
        {
            VocabularyBook.Save(VocabularyPath, Vocabulary);
            SetStatus("vocabulary.json saved.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save vocabulary.json: {ex.Message}", isError: true);
        }
        NotifyStateChanged();
    }

    /// <summary>Saves all three files at once (the footer "Save changes" button).</summary>
    public void SaveAll()
    {
        SaveConfig();
        SavePhrases();
        SaveVocabulary();
        SetStatus("All changes saved.", isError: false);
        NotifyStateChanged();
    }

    // ---------------------------------------------------------------------------------------
    // Profiles (CH3)
    // ---------------------------------------------------------------------------------------

    /// <summary>The profile activated last, as a label. Null when none ever was.</summary>
    public string? ActiveProfile => _settings.ActiveProfile;

    /// <summary>
    /// Whether the live config.json still holds what the active profile holds. False while
    /// there is no active profile, so the editor can distinguish "Training, unchanged" from
    /// "Training, edited since" rather than claiming a profile is in effect either way.
    /// </summary>
    public bool ActiveProfileStillMatches =>
        ActiveProfile != null && ConfigProfiles.MatchesLiveConfig(ConfigFolder, ActiveProfile);

    public List<ConfigProfile> Profiles()
    {
        try
        {
            return ConfigProfiles.List(ConfigFolder);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read the Profiles folder: {ex.Message}", isError: true);
            return new List<ConfigProfile>();
        }
    }

    /// <summary>
    /// Writes the settings currently in the editor to a profile of that name.
    ///
    /// Saves what is on screen, not what is in config.json - unsaved edits are usually the whole
    /// reason for making a profile, and the alternative (save, then profile the saved file) means
    /// overwriting the live config to record a variant of it.
    /// </summary>
    public void SaveProfile(string? name)
    {
        var problem = ConfigProfiles.NameProblem(name);
        if (problem != null)
        {
            SetStatus(problem, isError: true);
            NotifyStateChanged();
            return;
        }

        try
        {
            ConfigProfiles.Save(ConfigFolder, name, Config);
            SetStatus($"Profile \"{ConfigProfiles.Normalize(name)}\" saved from the settings on screen.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save the profile: {ex.Message}", isError: true);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Copies a profile over config.json and reloads the editor from it, so what is on screen is
    /// what the bot will now read.
    /// </summary>
    public void ActivateProfile(string? name)
    {
        try
        {
            ConfigProfiles.Activate(ConfigFolder, name);
            Remember(s => s.ActiveProfile = ConfigProfiles.Normalize(name));

            Load();

            // Load() sets its own "Loaded from ..." message; this is the one worth keeping.
            if (IsLoaded)
                SetStatus($"Profile \"{ConfigProfiles.Normalize(name)}\" is now config.json. " +
                          "Restart the bot for it to take effect.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not activate the profile: {ex.Message}", isError: true);
        }

        NotifyStateChanged();
    }

    public void DeleteProfile(string? name)
    {
        try
        {
            ConfigProfiles.Delete(ConfigFolder, name);

            if (string.Equals(ActiveProfile, ConfigProfiles.Normalize(name), StringComparison.OrdinalIgnoreCase))
                Remember(s => s.ActiveProfile = null);

            SetStatus($"Profile \"{ConfigProfiles.Normalize(name)}\" deleted; a copy is in Profiles\\Backup.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not delete the profile: {ex.Message}", isError: true);
        }

        NotifyStateChanged();
    }

    // ---------------------------------------------------------------------------------------
    // TTS voices
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The TTS voices this machine reported, shared between the Radios and Speech panels: asking
    /// costs a process start, and both panels want the same answer. Empty until loaded.
    /// </summary>
    public List<TtsVoice> TtsVoiceList { get; private set; } = new();

    /// <summary>What happened on the last attempt, for the panels to show. Null = never asked.</summary>
    public string? TtsVoiceStatus { get; private set; }

    public bool TtsVoiceStatusIsError { get; private set; }
    public bool TtsVoicesLoading { get; private set; }

    /// <summary>
    /// Asks DCS-SR-ExternalAudio.exe which voices it can use. Cached: a second call returns the
    /// previous answer unless <paramref name="force"/> is set, so switching panels doesn't start
    /// the tool again.
    /// </summary>
    public async Task LoadTtsVoicesAsync(bool force = false)
    {
        if (TtsVoicesLoading) return;
        if (!force && TtsVoiceList.Count > 0) return;

        TtsVoicesLoading = true;
        TtsVoiceStatus = "Asking DCS-SR-ExternalAudio.exe...";
        TtsVoiceStatusIsError = false;
        NotifyStateChanged();

        try
        {
            var result = await TtsVoices.ListAsync(Config.ExternalAudioExePath);

            if (result.Ok)
            {
                TtsVoiceList = result.Voices;
                TtsVoiceStatus = $"{result.Voices.Count} voice(s) available on this machine.";
                TtsVoiceStatusIsError = false;
            }
            else
            {
                TtsVoiceStatus = result.Error;
                TtsVoiceStatusIsError = true;
            }
        }
        catch (Exception ex)
        {
            // TtsVoices.ListAsync already turns failures into a message; this is the belt-and-braces
            // case, since an exception escaping here would take the whole editor down.
            TtsVoiceStatus = $"Could not list the voices: {ex.Message}";
            TtsVoiceStatusIsError = true;
        }
        finally
        {
            TtsVoicesLoading = false;
            NotifyStateChanged();
        }
    }

    /// <summary>Reads the tail of the newest log file under ConfigFolder/logs, for the Logging panel's live preview.</summary>
    public List<string> GetRecentLogLines(int maxLines = 12)
    {
        try
        {
            var logsDir = Path.Combine(ConfigFolder, "logs");
            if (!Directory.Exists(logsDir)) return new List<string>();

            var newest = LogFiles.PickNewest(Directory.GetFiles(logsDir, "*.log"));
            if (newest == null) return new List<string>();

            var lines = LogFiles.ReadAllLinesShared(newest);
            return lines.Count <= maxLines ? lines : lines.Skip(lines.Count - maxLines).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>The log file the tail is currently reading, so the panel can show which one it is.</summary>
    public string? CurrentLogFile
    {
        get
        {
            try
            {
                var logsDir = Path.Combine(ConfigFolder, "logs");
                return Directory.Exists(logsDir) ? LogFiles.PickNewest(Directory.GetFiles(logsDir, "*.log")) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Writes a DCS-gRPC explorer result to ConfigFolder/grpc-dumps/ and returns the full path.
    /// Lives here rather than in the Razor component because WPF projects drop System.IO from the
    /// implicit usings (clash with System.Windows.Shapes.Path).
    /// </summary>
    public string SaveGrpcDump(string queryId, string json)
    {
        var dir = Path.Combine(ConfigFolder, "grpc-dumps");
        Directory.CreateDirectory(dir);
        var safeId = string.Concat(queryId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeId}.json");
        File.WriteAllText(file, json);
        return file;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
