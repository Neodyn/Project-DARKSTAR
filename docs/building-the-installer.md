# Building the D.A.R.K.S.T.A.R. installer

> **Just want to install the bot?** Download `DARKSTAR-Setup-<version>.exe` from the Releases page
> instead — see [manual-en.md, chapter 3](manual-en.md#3-installation). This page is about
> *producing* that file.

*Part of the D.A.R.K.S.T.A.R. documentation — see the [manual](manual-en.md).*

This produces a normal Windows `Setup.exe` (via [Inno Setup](https://jrsoftware.org/isinfo.php),
free) that installs the bot (and optionally the config GUI + Vosk model + Windows Service) on a
target server, **including every runtime dependency both need** - the target machine only needs
to be Windows itself, nothing has to be pre-installed by hand.

## The short way: one command

From the repository root, in PowerShell:

```powershell
.\build-installer.ps1
```

That script does everything below for you: it checks the prerequisites, publishes the bot and the
GUI **in Release** (never Debug), downloads the three dependency installers and a Vosk model, and
compiles the `.iss` file in `installer\`.
The result is `installer\output\DARKSTAR-Setup-<version>.exe`.

| Parameter | Default | Purpose |
|---|---|---|
| `-VoskModelSize` | `Small` | Which model gets bundled. `Standard` is the recommended one for real use but makes a ~1.8 GB installer. |
| `-Version` | `1.0` | Stamped into the installer and its file name. |
| `-Slim` / `-SkipVoskModel` | off | Slim installer without a bundled model: a few MB instead of up to ~2 GB. Produces `DARKSTAR-Setup-<version>-slim.exe`; `VoskModelPath` then has to be set by hand on the target machine. |
| `-SkipDependencyDownload` | off | Don't fetch the dependency installers; anything missing simply isn't bundled. |
| `-Clean` | off | Delete previous publish output and installers first. |
| `-DryRun` | off | Only check prerequisites and print the plan. |

It is safe to re-run: every step skips itself when its result is already in place. To swap the
bundled model for a different size, delete `installer\vosk-model\` first.

Needed on the build machine: the **.NET 8 SDK** and **Inno Setup 6** (the script offers to install
Inno Setup via winget when it's missing).

## Doing it by hand

## One-time setup

1. Install [Inno Setup](https://jrsoftware.org/isdl.php) on your build machine (not the target
   server - this is only needed to *compile* the installer).
2. Download the three dependency installers once and place them directly in the `installer\`
   folder (all official Microsoft download links - safe to re-download periodically to pick up
   updates):

   | Dependency | Needed by | Download | Save as |
   |---|---|---|---|
   | .NET 8 Desktop Runtime (x64) | Bot **and** GUI (superset of the plain .NET 8 runtime the bot needs) | https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe | `installer\WindowsDesktopRuntime8-x64.exe` |
   | Visual C++ Redistributable (x64) | Bot (native `libvosk.dll` used by Vosk wake-word detection) | https://aka.ms/vs/17/release/vc_redist.x64.exe | `installer\VC_redist.x64.exe` |
   | WebView2 Evergreen Bootstrapper | GUI (renders the Blazor UI) | https://developer.microsoft.com/microsoft-edge/webview2/ → "Evergreen Bootstrapper" | `installer\MicrosoftEdgeWebview2Setup.exe` |

   Any of the three missing at compile time is fine (`skipifsourcedoesntexist`) - that dependency's
   install step is simply skipped. A normal build should have all three present, though.

## Every time you build a new installer version

1. Publish both projects **framework-dependent** (not self-contained - the dependency installers
   above now cover the runtime, keeping the published output small):
   ```
   cd C:\BOT_SRS
   dotnet publish Darkstar.csproj                   -c Release -r win-x64 --self-contained false -o installer\publish\bot
   dotnet publish Darkstar.Gui\Darkstar.Gui.csproj   -c Release -r win-x64 --self-contained false -o installer\publish\gui
   ```
2. Download a Vosk model (https://alphacephei.com/vosk/models, e.g.
   `vosk-model-small-en-us-0.15`) and unpack it so that
   `installer\vosk-model\am\final.mdl` (and its sibling files/folders) exist directly under
   `installer\vosk-model\` - i.e. unpack the model's own top folder's *contents* there, not the
   model as one more nested folder.
3. Open `installer\Setup.iss` in Inno Setup and click **Compile** (or `Build > Compile`).
4. The finished installer appears at `installer\output\DARKSTAR-Setup.exe`.

## What the installer actually does

- Lets the person installing choose: bot only (headless server), bot + GUI, or fully custom
  (individual components: bot / GUI / Vosk model).
- Copies the published, framework-dependent bot and GUI, then installs whichever of the
  following dependencies aren't already present on the target machine (each check is
  skip-if-already-installed, so re-running the installer is always safe):
  - **.NET 8 Desktop Runtime** — always checked/installed if anything is being installed at all,
    since it covers both the bot's plain runtime need and the GUI's WPF/Blazor Hybrid need.
  - **Visual C++ Redistributable (x64)** — checked/installed only when the bot component is
    selected (it's what Vosk's native `libvosk.dll` needs at runtime; .NET publishing, even
    self-contained, doesn't bring this along since it isn't a .NET assembly).
  - **WebView2 Runtime** — checked/installed only when the GUI component is selected. Most
    current Windows 10/11 machines already have it.
- **Writes a `config.json` with the two paths it can work out itself** - this is exactly the
  manual fixing you had to do by hand after moving the project:
  - `VoskModelPath`, pointing at the model it just installed (only when that component was
    selected, so a `-slim` install leaves it out).
  - `ExternalAudioExePath`, pointing at this machine's real SRS installation. It reads SRS's
    own uninstall entry from the registry (`HKLM64`, `HKLM32`, `HKCU`, matching any
    `DisplayName` containing "SimpleRadio") and takes its `InstallLocation`; failing that it
    probes both `Program Files` folders, `%LOCALAPPDATA%\Programs` and the root,
    `Program Files` and `Games` folder of every drive from C: to Z: for the known layouts.
    The layout and folder-name lists mirror `Darkstar.Core\SrsPaths.cs`, which does the same
    at runtime behind the GUI's "Detect SRS installation" button - `srstest` asserts that the
    two lists stay in sync.

  It runs at `ssPostInstall` for every variant (the slim one included, which still has the SRS
  path to seed), writes nothing at all when neither value could be determined, and never
  touches an existing `config.json`. Every other setting still gets its normal default via the
  bot's own `AppConfig.LoadOrCreateDefault` on first run (and is merged into this file, same as
  usual) - the installer does not try to guess things it can't know, like your SRS server
  address or your Gemini API key.
- Optional checkbox on the "Ready to Install" page: register the bot as a Windows Service
  (unchecked by default, since silently starting background services isn't something an
  installer should assume you want).
- Adds a Start Menu shortcut for the config GUI, and a clean uninstaller that also removes the
  Windows Service if one was installed.

## Internet access on the target machine

None of the three dependency installers download anything at install time - they're bundled
into `Setup.exe` at compile time and simply run locally. The target machine only needs internet
access if you'd rather have Windows/Windows Update handle these separately; the installer itself
works fully offline once built.

## Known uncertainty

The WebView2 and Visual C++ Redistributable "already installed?" checks use the registry keys
Microsoft documents them to register under. This is the standard approach used by most
installers, but registry layouts can occasionally shift between versions - if a bootstrapper runs
even though the dependency is already present, that's harmless (it just no-ops), so every check
here fails safe either way. The .NET Desktop Runtime check looks for its shared-framework folder
under `Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*` rather than the registry,
which is the same approach `dotnet --list-runtimes` effectively relies on.
