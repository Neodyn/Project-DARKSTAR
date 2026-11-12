namespace Darkstar.Gui;

/// <summary>
/// Opening Windows' own folder and file pickers from a panel.
///
/// An interface rather than a direct call, for one practical reason: the dialogs are WPF types, and
/// the panels are Razor components that the offline build harness compiles against plain
/// <c>net8.0</c> with no WPF at all. A panel that newed up an <c>OpenFolderDialog</c> could not be
/// compile-checked there, which is most of what that harness is for. This way the panels depend on
/// three method signatures and the WPF code sits in one file beside them
/// (<c>WindowsPathPicker</c>), registered in <c>App.xaml.cs</c>.
/// </summary>
public interface IPathPicker
{
    /// <summary>
    /// Asks for a folder, starting at <paramref name="startAt"/> when that folder exists.
    /// Returns null when the dialog was cancelled or could not be shown - the caller leaves the
    /// field as it was.
    /// </summary>
    string? PickFolder(string title, string? startAt);

    /// <summary>
    /// Asks for an existing file. <paramref name="filter"/> is a Win32 dialog filter, e.g.
    /// <c>"Programs|*.exe|All files|*.*"</c>. <paramref name="startAt"/> may be a file or a
    /// folder; a file's own folder is used, and the file is preselected.
    /// </summary>
    string? PickFile(string title, string? startAt, string filter);

    /// <summary>Common filters, kept here so two panels asking for the same thing agree.</summary>
    public static class Filters
    {
        public const string Executables = "Programs|*.exe|All files|*.*";
        public const string WhisperModels = "Whisper models|*.bin|All files|*.*";
    }
}
