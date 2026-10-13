namespace Darkstar;

/// <summary>What is wrong (or right) with a configured Vosk model folder.</summary>
public enum VoskModelStatus
{
    /// <summary>The folder holds a usable Vosk model.</summary>
    Ok,

    /// <summary>No path configured at all - the bot falls back to the volume placeholder detector.</summary>
    NotConfigured,

    /// <summary>The configured folder doesn't exist on disk.</summary>
    FolderMissing,

    /// <summary>The folder exists but doesn't contain a Vosk model.</summary>
    NotAModelFolder,
}

/// <summary>The outcome of checking a model folder, with a message meant to be read by a human.</summary>
public sealed class VoskModelCheckResult
{
    public VoskModelStatus Status { get; init; } = VoskModelStatus.Ok;

    /// <summary>
    /// The folder that should actually be handed to Vosk. Usually the configured path, but when
    /// the model sits one level deeper (the classic "unpacked the archive into a folder of its
    /// own" case) this is that subfolder. Null unless Status is Ok.
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>One line stating what is wrong. Empty when Status is Ok.</summary>
    public string Message { get; init; } = "";

    /// <summary>Concrete things the user can do about it, one per line. Empty when Status is Ok.</summary>
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();

    public bool IsUsable => Status == VoskModelStatus.Ok;
}

/// <summary>
/// Checks a Vosk model folder BEFORE it is handed to the native library.
///
/// This is not a nicety. Vosk's native <c>vosk_model_new</c> returns a null pointer when it
/// can't load a model, the C# wrapper stores that null happily, and the crash only happens
/// later, when the first recognizer is created from it - as an
/// <see cref="AccessViolationException"/>, which .NET cannot catch. The process dies with a
/// native stack trace and no hint that the real problem was a missing folder. So the only place
/// a bad model can still be reported properly is before that call.
///
/// Lives in Darkstar.Core (which doesn't reference the Vosk package) so the bot, the config
/// editor and the tests can all use it. The part that needs Vosk itself - verifying that the
/// loaded model really got a native handle - is in VoskHotwordDetector.LoadModel.
/// </summary>
public static class VoskModelCheck
{
    /// <summary>
    /// Files that mark a folder as a Vosk model. Every current model has an acoustic model under
    /// <c>am\</c> and a configuration under <c>conf\</c>; checking for the files rather than just
    /// the folders also catches a half-finished download.
    /// </summary>
    private static readonly string[][] MarkerFiles =
    {
        new[] { "am", "final.mdl" },
        new[] { "conf", "model.conf" },
        new[] { "conf", "mfcc.conf" },
    };

    /// <summary>How many subfolders deep to look for a model that was unpacked one level too far.</summary>
    private const int MaxSubfoldersToInspect = 24;

    public static VoskModelCheckResult Check(string? configuredPath) =>
        Check(configuredPath, Directory.Exists, File.Exists, SafeEnumerateDirectories);

    /// <summary>Testable core of <see cref="Check(string?)"/>, with the file system injected.</summary>
    internal static VoskModelCheckResult Check(
        string? configuredPath,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new VoskModelCheckResult
            {
                Status = VoskModelStatus.NotConfigured,
                Message = "VoskModelPath is not set in config.json - there is no wake word detection.",
                Hints = new[]
                {
                    "Install the model with the full installer, or download one from https://alphacephei.com/vosk/models,",
                    "unpack it and enter the unpacked folder in the config editor under CH3 Speech.",
                },
            };
        }

        var path = configuredPath.Trim().Trim('"');

        if (!directoryExists(path))
        {
            return new VoskModelCheckResult
            {
                Status = VoskModelStatus.FolderMissing,
                Message = $"The Vosk model folder '{path}' does not exist.",
                Hints = new[]
                {
                    "Check VoskModelPath in config.json (config editor, CH3 Speech).",
                    "After a slim install the model has to be downloaded separately: https://alphacephei.com/vosk/models",
                },
            };
        }

        if (LooksLikeModel(path, fileExists))
            return new VoskModelCheckResult { Status = VoskModelStatus.Ok, ResolvedPath = path };

        // The most common mistake by far: the path points at the folder the archive was unpacked
        // into, and the model itself is one level below it. Rather than refusing over a detail we
        // can see through, use the subfolder - and say so, so the config can be corrected.
        var inner = FindModelSubfolder(path, fileExists, enumerateDirectories);
        if (inner != null)
        {
            return new VoskModelCheckResult
            {
                Status = VoskModelStatus.Ok,
                ResolvedPath = inner,
                Message = $"VoskModelPath points at '{path}', but the model itself is in '{inner}' - using that.",
                Hints = new[] { "Set VoskModelPath to that folder to get rid of this message." },
            };
        }

        return new VoskModelCheckResult
        {
            Status = VoskModelStatus.NotAModelFolder,
            Message = $"The folder '{path}' exists but does not contain a Vosk model (no am\\final.mdl and no conf\\model.conf).",
            Hints = new[]
            {
                "A model folder contains the subfolders am, conf, graph and ivector.",
                "If the download was interrupted, unpack the archive again.",
                "Models: https://alphacephei.com/vosk/models (e.g. vosk-model-small-en-us-0.15)",
            },
        };
    }

    /// <summary>True if this folder is itself a Vosk model.</summary>
    public static bool LooksLikeModel(string path) => LooksLikeModel(path, File.Exists);

    internal static bool LooksLikeModel(string path, Func<string, bool> fileExists)
    {
        foreach (var marker in MarkerFiles)
        {
            var full = path;
            foreach (var segment in marker)
                full = Path.Combine(full, segment);

            try
            {
                if (fileExists(full))
                    return true;
            }
            catch
            {
                // An unreadable path is not a model as far as we are concerned.
            }
        }

        return false;
    }

    private static string? FindModelSubfolder(
        string path,
        Func<string, bool> fileExists,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        var inspected = 0;
        foreach (var sub in enumerateDirectories(path))
        {
            if (++inspected > MaxSubfoldersToInspect)
                break;

            if (LooksLikeModel(sub, fileExists))
                return sub;
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }
}
