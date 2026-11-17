using System.Windows;
using System.Windows.Media;

namespace Darkstar.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The window shows its own background for a moment before the page has loaded. Dark in
        // the XAML; with the light theme chosen it would flash dark at every start, so the
        // window takes the page's background colour (--bg in app.css) from the same setting.
        if (EditorSettings.Load(EditorSettings.DefaultPath()).IsLight)
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF6));
    }
}
