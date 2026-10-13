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
    /// Folder containing config.json/phrases.json/vocabulary.json. Defaults to wherever the
    /// bot's own config.json is actually found (see ResolveDefaultConfigFolder) so the GUI and
    /// the bot service see the same files out of the box, with no manual setup - change it via
    /// the Connection page if the bot lives somewhere unusual.
    /// </summary>
    public string ConfigFolder { get; set; } = ResolveDefaultConfigFolder();

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
