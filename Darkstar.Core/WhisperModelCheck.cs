namespace Darkstar;

/// <summary>What is wrong (or right) with a configured Whisper model file.</summary>
public enum WhisperModelStatus
{
    /// <summary>The file is a usable ggml Whisper model.</summary>
    Ok,

    /// <summary>No path configured. Only a problem when Whisper is the selected engine.</summary>
    NotConfigured,

    /// <summary>Nothing at that path.</summary>
    FileMissing,

    /// <summary>A folder was given where a single .bin file is wanted, and nothing obvious inside it.</summary>
    FolderWithoutModel,

    /// <summary>The file is there but far too small to be any Whisper model - the usual half download.</summary>
    Truncated,

    /// <summary>The file does not start with the ggml magic, so whisper.cpp will refuse it.</summary>
    NotAModel,
}

/// <summary>The outcome of checking a model file, with a message meant to be read by a human.</summary>
public sealed class WhisperModelCheckResult
{
    public WhisperModelStatus Status { get; init; } = WhisperModelStatus.Ok;

    /// <summary>
    /// The file that should actually be handed to Whisper. Usually the configured path, but when a
    /// folder was given and exactly one model sits inside it, this is that file. Null unless Ok.
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>One line stating what is wrong. Empty when Status is Ok.</summary>
    public string Message { get; init; } = "";

    /// <summary>Concrete things the user can do about it, one per line. Empty when Status is Ok.</summary>
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();

    /// <summary>The model's size on disk, for telling a tiny model from a large one in the log.</summary>
    public long SizeBytes { get; init; }

    public bool IsUsable => Status == WhisperModelStatus.Ok;
}

/// <summary>
/// Checks a Whisper model file BEFORE it is handed to the native library, for the same reason
/// <see cref="VoskModelCheck"/> exists: a native loader that is handed a bad file reports it in a
/// way nobody can act on, somewhere far from the cause.
///
/// The failure worth catching is not the missing file - that one is obvious - but the HALF
/// DOWNLOADED one. A Whisper model is between 75 MB and 2.9 GB fetched over HTTP, and an
/// interrupted transfer leaves a file that exists, has a plausible name, and is refused by
/// whisper.cpp with "invalid model data (bad magic)" buried in a native log. So the size and the
/// first four bytes are both checked here, where the message can name the file and say what to do.
///
/// Lives in Darkstar.Core, which references no Whisper package, so the bot, the config editor and
/// the tests all use the same check. Verifying that the native runtime itself loads is a separate
/// question and belongs with whatever holds the Whisper dependency.
/// </summary>
public static class WhisperModelCheck
{
    /// <summary>
    /// The four bytes every ggml model starts with.
    ///
    /// whisper.cpp reads them as a native-endian <c>uint32</c> and compares against
    /// <c>GGML_FILE_MAGIC</c> (0x67676d6c, "ggml" in hex), and the converter writes them with
    /// Python's <c>struct.pack("i", 0x67676d6c)</c>. On a little-endian machine - which is every
    /// machine this runs on - that puts the bytes on disk in the order <c>6c 6d 67 67</c>. A hex
    /// dump therefore reads "lmgg", not "ggml", and the next person to look at one should not have
    /// to work that out twice.
    /// </summary>
    public const uint GgmlMagic = 0x67676d6c;

    /// <summary>
    /// Below this, nothing can be a Whisper model: the smallest one published (<c>tiny</c>) is
    /// about 75 MB. Deliberately well under that rather than near it, so the message is "this file
    /// is truncated" only when it certainly is, and a future smaller model is not rejected.
    /// </summary>
    public const long MinimumPlausibleBytes = 20L * 1024 * 1024;

    /// <summary>Where the models come from, named in every hint that needs it.</summary>
    public const string ModelSource = "https://huggingface.co/ggerganov/whisper.cpp";

    /// <summary>The script in the whisper.cpp repository that fetches one on Windows.</summary>
    public const string DownloadScript =
        "https://github.com/ggml-org/whisper.cpp/blob/master/models/download-ggml-model.cmd";

    public static WhisperModelCheckResult Check(string? configuredPath) =>
        Check(configuredPath, File.Exists, Directory.Exists, SafeLength, SafeMagic, SafeEnumerateModels);

    /// <summary>Testable core, with the file system injected - no model on disk needed to test it.</summary>
    internal static WhisperModelCheckResult Check(
        string? configuredPath,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, long> lengthOf,
        Func<string, uint?> magicOf,
        Func<string, IReadOnlyList<string>> modelsIn)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new WhisperModelCheckResult
            {
                Status = WhisperModelStatus.NotConfigured,
                Message = "No Whisper model is configured.",
                Hints = new[]
                {
                    $"Download one with {DownloadScript} (for example \"small.en\"), or straight from {ModelSource}.",
                    "Then point WhisperModelPath at the ggml-*.bin file.",
                },
            };
        }

        var path = configuredPath.Trim().Trim('"');

        // A folder is the obvious mistake when the download script leaves the file in a models\
        // directory. One model inside is unambiguous, so it is used; several is not, so it isn't.
        if (!fileExists(path) && directoryExists(path))
        {
            var found = modelsIn(path);

            if (found.Count == 1)
                return Inspect(found[0], lengthOf, magicOf, cameFromFolder: true);

            return new WhisperModelCheckResult
            {
                Status = WhisperModelStatus.FolderWithoutModel,
                Message = found.Count == 0
                    ? $"\"{path}\" is a folder and holds no ggml-*.bin model."
                    : $"\"{path}\" is a folder and holds {found.Count} models - name the one you want.",
                Hints = found.Count == 0
                    ? new[] { $"Download a model with {DownloadScript}, then point at the .bin file itself." }
                    : found.Take(4).Select(f => $"For example: {f}").ToArray(),
            };
        }

        if (!fileExists(path))
        {
            return new WhisperModelCheckResult
            {
                Status = WhisperModelStatus.FileMissing,
                Message = $"No Whisper model at \"{path}\".",
                Hints = new[]
                {
                    "Check the path for a typo, or whether the download finished.",
                    $"Models come from {ModelSource}; {DownloadScript} fetches one on Windows.",
                },
            };
        }

        return Inspect(path, lengthOf, magicOf, cameFromFolder: false);
    }

    private static WhisperModelCheckResult Inspect(string path, Func<string, long> lengthOf,
        Func<string, uint?> magicOf, bool cameFromFolder)
    {
        var size = lengthOf(path);

        // Size first: a truncated download usually also fails the magic test, but not always - the
        // first four bytes arrive before the rest does - and "the file is 3 MB" is a far more
        // actionable sentence than "bad magic".
        if (size < MinimumPlausibleBytes)
        {
            return new WhisperModelCheckResult
            {
                Status = WhisperModelStatus.Truncated,
                SizeBytes = size,
                Message = $"\"{path}\" is only {size / (1024.0 * 1024.0):0.#} MB - too small for any Whisper model.",
                Hints = new[]
                {
                    "The download was almost certainly interrupted. Delete the file and fetch it again.",
                    "For reference: tiny is about 75 MB, base 142 MB, small 466 MB, medium 1.5 GB.",
                },
            };
        }

        var magic = magicOf(path);
        if (magic != GgmlMagic)
        {
            return new WhisperModelCheckResult
            {
                Status = WhisperModelStatus.NotAModel,
                SizeBytes = size,
                Message = $"\"{path}\" does not start with the ggml marker, so Whisper will refuse it.",
                Hints = new[]
                {
                    "This is what a half-finished download or an HTML error page saved under a .bin name looks like.",
                    $"Fetch it again from {ModelSource} - the file must be named ggml-<model>.bin.",
                },
            };
        }

        return new WhisperModelCheckResult
        {
            Status = WhisperModelStatus.Ok,
            ResolvedPath = path,
            SizeBytes = size,
            Message = cameFromFolder ? $"Using the only model in that folder: {path}" : "",
        };
    }

    /// <summary>A one-line summary for the startup log and the GUI.</summary>
    public static string Describe(WhisperModelCheckResult result) =>
        result.IsUsable
            ? $"Whisper model ready ({result.SizeBytes / (1024.0 * 1024.0):0.#} MB): {result.ResolvedPath}"
            : result.Message;

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// The first four bytes as a little-endian uint32, or null when the file cannot be read far
    /// enough. Null is treated as "not a model" by the caller, which is right: a file that cannot
    /// be read is not one that can be loaded either.
    /// </summary>
    private static uint? SafeMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) < 4
                ? null
                : (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SafeEnumerateModels(string folder)
    {
        try { return Directory.GetFiles(folder, "ggml-*.bin").OrderBy(f => f).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
