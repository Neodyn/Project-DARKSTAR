using Darkstar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// IMPORTANT: use the executable's own directory for the log folder, not the current working
// directory - a Windows Service starts with C:\Windows\System32 as its working directory by
// default, which would otherwise put the logs in the wrong place.
Logger.Init(Path.Combine(AppContext.BaseDirectory, "logs"));
Logger.Log("D.A.R.K.S.T.A.R. - Digital Assistant for Radio Keyword-activated Speech Transcription And Response");

// "--test-hotword" measures wake word detection against recorded audio and exits. It never
// connects to SRS, so it is safe to run while the service is live - useful for checking whether
// a different model or audio setting would do better on the traffic you actually get.
if (HotwordTestRunner.IsRequested(args))
    return HotwordTestRunner.Run(args);

var builder = Host.CreateApplicationBuilder(args);

// Makes the exact same .exe runnable both as a normal console app (double-click / `dotnet run`,
// useful for testing) and as a real Windows Service (via sc.exe / services.msc) - the host
// automatically detects which mode it's running in and wires up the correct start/stop
// lifecycle either way. See README for installation instructions.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "D.A.R.K.S.T.A.R.";
});

// We already have our own Logger (console + file) - skip the Generic Host's default console
// logging provider to avoid duplicate/cluttered startup output.
builder.Logging.ClearProviders();

builder.Services.AddHostedService<BotService>();

// Last line of defence: anything that escapes the service itself still gets written to the log
// file as something readable, rather than only appearing as a runtime crash dump in a console
// window that a Windows Service doesn't even have.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        Logger.Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
        Logger.Debug(ex.ToString());
    }
    else
    {
        Logger.Log($"FATAL: {e.ExceptionObject}");
    }

    Logger.Log("The bot is shutting down. Full details are in the log file under logs\\.");

    // The process is about to die: get these lines onto disk now, they are the ones that matter.
    Logger.Flush();
};

// A faulted fire-and-forget task (a reply transmission, a threat circle sweep) would otherwise
// be silently swallowed - or, depending on configuration, tear the process down at some later
// garbage collection with no context at all.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Logger.Log($"WARNING: a background task failed without being awaited: {e.Exception.GetBaseException().Message}");
    Logger.Debug(e.Exception.ToString());
    e.SetObserved();
};

try
{
    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    // Host construction/startup failures (a locked log file, a broken service registration)
    // land here. The bot's own startup problems are handled inside BotService, which logs them
    // in full and stops the host cleanly.
    Logger.Log($"ERROR: the bot could not be started - {ex.GetType().Name}: {ex.Message}");
    Logger.Debug(ex.ToString());
    Logger.Shutdown();
    return BotService.ExitCodeStartupFailure;
}

Logger.Shutdown();
return Environment.ExitCode;
