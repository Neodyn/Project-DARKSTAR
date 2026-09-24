using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace Darkstar.Gui;

public partial class App : Application
{
    public App()
    {
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
