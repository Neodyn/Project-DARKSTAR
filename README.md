# D.A.R.K.S.T.A.R.

**D**igital **A**ssistant for **R**adio **K**eyword-activated **S**peech **T**ranscription **A**nd **R**esponse

*🇩🇪 [Diese Seite auf Deutsch](README.de.md) · 📖 [Full manual](docs/manual-en.md)*

A voice-controlled radio assistant for [DCS World](https://www.digitalcombatsimulator.com/) that joins a [DCS-SimpleRadio-Standalone](https://github.com/ciribob/DCS-SimpleRadio-Standalone) (SRS) server as an external AWACS-mode client.

A pilot keys up on a monitored frequency, says the wake word and asks something — the bot transcribes it, works out an answer, and transmits it back in a synthetic voice.

> *"Overlord, bogey dope."*
> *"Enfield 1-1, this is Overlord… Bogey, bearing zero, niner, zero, thirty five miles, twenty two thousand, hot, group of two, type MiG-29."*

---

## What it can do

- **Offline wake word** — [Vosk](https://alphacephei.com/vosk/) transcribes locally and watches for your keyword. No account, no per-request cost, and no audio leaves the machine for this step.
- **Answers from the running mission** — "bogey dope", "picture" and "threat check" are answered from live DCS data via [DCS-gRPC](https://github.com/DCS-gRPC/rust-server): BRAA with aspect from the pilot's own aircraft, or bullseye format.
- **Threat circle** — a pilot arms a standing watch around their own aircraft ("threat circle forty miles") and gets warned as soon as a hostile enters it.
- **Fixed phrases and free answers** — known question/answer pairs are served directly; anything else can go to Google Gemini (or be refused, your choice).
- **Several radios at once** — each frequency with its own wake word, callsign and conversation, e.g. "Overlord" on AWACS and "Texaco" on the tanker.
- **Coalition awareness** — can ignore the opposing side entirely, the way a real radio would.
- **Config editor** — a desktop GUI for every setting, with a DCS-gRPC connection test, a read-only mission data explorer, and one-click Windows Service management.
- **One-command installer build** — produces a `Setup.exe` that installs every runtime dependency on a bare Windows machine.

## Quick start

```powershell
# 1. Set up the dev machine (SDK, workloads, runtimes, a speech model)
.\setup-dev-environment.ps1 -ProjectRoot "C:\path\to\this\repo" -VoskModelSize Standard

# 2. First run: creates config.json next to the executable and exits
dotnet run --project Darkstar.csproj

# 3. Fill in SRS server, radios, VoskModelPath and GeminiApiKey - by hand or in the GUI - then run it again
```

Deploying to another machine instead? Build the installer:

```powershell
.\build-installer.ps1                      # with a bundled speech model
.\build-installer.ps1 -Slim                # small installer, model supplied separately
```

Everything in detail — requirements, every setting, radio usage, DCS-gRPC setup, troubleshooting — is in the **[full manual](docs/manual-en.md)**.

## Documentation

| Document | What's in it |
|---|---|
| **[Manual](docs/manual-en.md)** | The complete guide: requirements, installation, configuration, radio usage, DCS-gRPC, Windows Service, troubleshooting. |
| [Configuration reference](docs/configuration.md) | Every field of `config.json`, `phrases.json` and `vocabulary.json`. |
| [Config editor (GUI)](docs/gui.md) | All nine channel panels and the Blazor Hybrid build requirements. |
| [Building the installer](docs/building-the-installer.md) | How `build-installer.ps1` and the Inno Setup script fit together. |
| [Contributing](docs/contributing.md) | Ground rules and project layout for working on the code. |
| [Changelog](docs/changelog.md) | What changed. |
| 🇩🇪 [Handbuch (Deutsch)](docs/handbuch-de.md) | Dieselbe vollständige Dokumentation auf Deutsch. |

## Requirements

Windows, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), a running SRS server with its `DCS-SR-ExternalAudio.exe`, a [Vosk model](https://alphacephei.com/vosk/models) and a [Gemini API key](https://aistudio.google.com/apikey) (free tier is enough). Optional: a [DCS-gRPC](https://github.com/DCS-gRPC/rust-server) server for the tactical replies. The [manual](docs/manual-en.md#2-what-you-need) explains each of them.

## Project structure

```
Darkstar.csproj, *.cs        The bot: SRS connection, audio, wake word, replies
Darkstar.Core/               Shared library: config, phrases, logging, DCS-gRPC, service management
Darkstar.Gui/                Config editor (Blazor Hybrid over WPF)
installer/                   Inno Setup script
docs/                        All documentation
build-installer.ps1          Builds the distributable Setup.exe
setup-dev-environment.ps1    Sets up a development machine
```

`Darkstar.csproj` sits in the repository root on purpose — its project references point at `Darkstar.Core\` relative to itself, so moving it into a subfolder breaks the solution.

## License

GPL-3.0 — see [LICENSE](LICENSE).
