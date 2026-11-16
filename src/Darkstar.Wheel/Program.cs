namespace Darkstar.Wheel;

/// <summary>
/// Starts the radio wheel: one instance per PC, settings from %LOCALAPPDATA%\Darkstar\wheel.json,
/// and from then on an icon in the notification area. See <see cref="WheelApp"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Two wheels would both react to the same key and send every pick twice.
        using var single = new Mutex(true, "Darkstar.Wheel.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("The radio wheel is already running - its icon is in the notification area.",
                "D.A.R.K.S.T.A.R. Radio Wheel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Per-monitor aware, so the DCS window's size comes back in real pixels and the wheel is
        // centred on it even with Windows scaling at 125 or 150 %.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var path = WheelSettings.DefaultPath;
        var settings = WheelSettings.Load(path, out var problem);

        // Written once with the defaults, so "Open the settings file" has something to open.
        if (!File.Exists(path)) settings.Save(path);

        Application.Run(new WheelApp(settings, path, problem));
    }
}
