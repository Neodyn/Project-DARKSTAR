using System.Globalization;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace Darkstar.Gui;

public partial class App : Application
{
    public App()
    {
        // Numbers are formatted the same way on every machine. A frequency is "133.000 MHz"
        // whether the operator's Windows is English, German or Japanese: it is read off an F10
        // marker, typed into SRS and compared against config.json, none of which have a regional
        // setting. Without this, a German Windows writes "133,000 MHz" into the log and into the
        // generated tower notes - which is how this was found, by a test suite that was green here
        // and failed on the author's machine.
        //
        // Nothing this application shows is localized: the replies are English radio phraseology
        // and the editor's labels are hardcoded English, so there is nothing to lose by it. Blazor
        // already binds numeric inputs invariantly, so the editor's fields are unaffected.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var services = new ServiceCollection();

        // Registers the Blazor WebView infrastructure (WebView2-backed) so BlazorWebView
        // controls in XAML can render Razor components.
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        // Single shared instance across all Razor components/pages - holds the currently
        // loaded config.json/phrases.json/vocabulary.json content and knows how to save it
        // back (reusing Darkstar.Core, the same code the actual bot runs on).
        services.AddSingleton<ConfigStore>();

        Resources.Add("services", services.BuildServiceProvider());
    }
}
