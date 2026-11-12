using System.IO;
using System.Windows;
using Darkstar;
using Microsoft.Win32;

namespace Darkstar.Gui;

/// <summary>
/// The real folder and file pickers, through WPF's own dialogs.
///
/// <see cref="OpenFolderDialog"/> is new in .NET 8's WPF - before it, picking a folder meant
/// either the Windows Forms <c>FolderBrowserDialog</c> (a second UI framework referenced for one
/// dialog) or the old shell API. Since this project already targets .NET 8 and WPF, neither is
/// needed.
///
/// Two things this class is careful about:
///
/// A dialog has to be opened on the UI thread. Blazor's event handlers already run there in a
/// WebView host, but that is a property of the host rather than a guarantee, and getting it wrong
/// shows up as a dialog that never appears - so the call is marshalled explicitly when it needs to be.
///
/// And nothing here may throw. A failure to show a picker is a small inconvenience - the text box
/// beside it still works - whereas an exception out of a Blazor event handler takes the editor's
/// whole render loop with it, which would turn "the browse button is broken" into "the editor is
/// broken".
/// </summary>
public sealed class WindowsPathPicker : IPathPicker
{
    public string? PickFolder(string title, string? startAt) => Show(() =>
    {
        // Only Title, InitialDirectory and FolderName are used: those are the three the WPF
        // documentation shows, and single selection is already the default, so there is nothing
        // to gain from setting Multiselect and something to lose if it is named differently.
        var dialog = new OpenFolderDialog { Title = title };

        var start = ExistingFolder(startAt);
        if (start != null) dialog.InitialDirectory = start;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    });

    public string? PickFile(string title, string? startAt, string filter) => Show(() =>
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        // A configured path is usually a file; its folder is the useful starting point, and
        // preselecting the file itself makes "I only want to correct this" one click.
        if (!string.IsNullOrWhiteSpace(startAt))
        {
            var folder = ExistingFolder(startAt) ?? ExistingFolder(Path.GetDirectoryName(startAt));
            if (folder != null) dialog.InitialDirectory = folder;

            try
            {
                if (File.Exists(startAt)) dialog.FileName = Path.GetFileName(startAt);
            }
            catch (Exception)
            {
                // A path with characters the file system rejects. Not worth a word.
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    });

    /// <summary>The path if it is a folder that exists, otherwise null. Never throws.</summary>
    private static string? ExistingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the dialog on the UI thread and turns any failure into "nothing was chosen".
    /// </summary>
    private static string? Show(Func<string?> dialog)
    {
        try
        {
            var app = Application.Current;

            if (app == null || app.Dispatcher.CheckAccess())
                return dialog();

            return app.Dispatcher.Invoke(dialog);
        }
        catch (Exception ex)
        {
            // Logged rather than swallowed silently: a picker that never opens is otherwise
            // indistinguishable from one that was cancelled.
            Logger.Log($"The file picker could not be opened ({ex.Message}) - type the path instead.");
            return null;
        }
    }
}
