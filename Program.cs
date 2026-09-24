using Darkstar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// IMPORTANT: use the executable's own directory for the log folder, not the current working
// directory - a Windows Service starts with C:\Windows\System32 as its working directory by
// default, which would otherwise put the logs in the wrong place.
Logger.Init(Path.Combine(AppContext.BaseDirectory, "logs"));
Logger.Log("D.A.R.K.S.T.A.R. - Digital Assistant for Radio Keyword-activated Speech Transcription And Response");

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

var host = builder.Build();
await host.RunAsync();
