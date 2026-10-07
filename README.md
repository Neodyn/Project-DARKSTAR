<p align="center">
    <img src="darkstar2.jpg" width="480" alt="D.A.R.K.S.T.A.R.">
</p>

# D.A.R.K.S.T.A.R.

**D**igital **A**ssistant for **R**adio **K**eyword-activated **S**peech **T**ranscription **A**nd **R**esponse

*🇩🇪 [Diese Seite auf Deutsch](README.de.md)*

A voice-controlled radio assistant for [DCS World](https://www.digitalcombatsimulator.com/) that joins a [DCS-SimpleRadio-Standalone](https://github.com/ciribob/DCS-SimpleRadio-Standalone) (SRS) server as an external AWACS-mode client.

A pilot keys up on a monitored frequency, says the wake word and asks something — the bot transcribes it, works out an answer, and transmits it back in a synthetic voice.

> *"Overlord, bogey dope."*
> *"Enfield 1-1, this is Overlord… Bogey, bearing zero, niner, zero, thirty five miles, twenty two thousand, hot, group of two, type MiG-29."*

## 📖 Documentation

**Everything is in the manual — installation, every setting, radio usage, troubleshooting:**

### → **[Full manual (English)](docs/manual-en.md)** · **[Vollständiges Handbuch (Deutsch)](docs/manual-de.md)**

Reference material: [configuration fields](docs/configuration.md) · [config editor](docs/gui.md) · [building the installer](docs/building-the-installer.md) · [contributing](docs/contributing.md) · [changelog](docs/changelog.md)

## Features

| | |
|---|---|
| 🎙️ **Offline wake word** | [Vosk](https://alphacephei.com/vosk/) transcribes locally and watches for your keyword. No account, no per-request cost, no audio leaving the machine for this step. |
| 📈 **Accuracy you can measure** | Proper anti-alias filtering on the detector's audio, plus `--test-hotword` to replay real recordings and count hits and misses instead of guessing. |
| 🗣️ **Built for non-native speakers** | A wake word can accept the spellings the recognizer really produces ("over lord" for "Overlord"), and `--suggest-variants` works that list out of your own recordings — flagging any variant that would cause false triggers. |
| 🛰️ **Answers from the live mission** | "bogey dope", "picture", "threat check" and "alpha check" answered from real DCS data via [DCS-gRPC](https://github.com/DCS-gRPC/rust-server) — BRAA with aspect from the pilot's own aircraft, or bullseye format. |
| 🤝 **Where's my flight?** | Optional, off by default: the position of another human player on your own side, measured from your aircraft. Never AI, never the other coalition, and never for a caller whose side can't be determined. |
| ✅ **Radio check that tells you something** | "Loud and clear" on every frequency, and — with mission data — whether the bot actually has you on scope. Answers before anything else can go wrong. |
| 🛫 **Runway in use and ATIS** | Live wind, temperature and pressure, plus the runway end the wind actually favours. |
| ⭕ **Threat circle** | A pilot arms a watch that flies with them ("threat circle forty miles") and gets warned the moment a hostile enters it. |
| 📻 **Several radios at once** | Each frequency with its own wake word, callsign, **voice**, conversation **and job** — tactical on the AWACS frequency, runway and ATIS on the tower. Three radios sound like three people. Call the wrong one and you get sent to the right one. |
| 💬 **Fixed phrases or free answers** | Known question/answer pairs served directly; anything else goes to Google Gemini, or is refused — your choice. |
| 🎯 **Realistic sensor gating** | Report only what a configured AI AWACS actually detects, or everything in the mission. |
| 🛡️ **Coalition aware** | Can ignore the opposing side entirely, the way a real radio net would. |
| 🖥️ **Config editor** | Desktop GUI for every setting, with a DCS-gRPC connection test, a read-only mission data explorer and one-click Windows Service management. |
| 📦 **One-file installer** | A `Setup.exe` that installs every runtime dependency on a bare Windows machine. |

## Installation

1. Download **`DARKSTAR-Setup-<version>.exe`** from the [Releases](../../releases) page.
2. Run it — it installs the bot, the config editor, a speech model and every missing runtime, and writes a `config.json` already pointing at the model and at your SRS installation.
3. Open the config editor, fill in your SRS server, radios and Gemini API key, save, start.

Step by step, with everything that can go wrong: **[manual, chapter 3](docs/manual-en.md#3-installation)**.

## Requirements

| | Needed for | Notes |
|---|---|---|
| **Windows** | everything | Replies use `DCS-SR-ExternalAudio.exe` and Windows TTS voices. |
| **SRS server** | everything | Including its `ExternalAudio\DCS-SR-ExternalAudio.exe`, which the bot transmits through — the installer finds it for you. |
| **[Gemini API key](https://aistudio.google.com/apikey)** | transcription, free answers | Free tier is enough for testing and small groups. |
| **[Vosk model](https://alphacephei.com/vosk/models)** | wake word | Comes with the installer. Offline, free, no account. |
| .NET 8 Desktop Runtime, VC++ Redistributable, WebView2 | running the bot and GUI | **Installed automatically** by the installer if missing. |
| **[DCS-gRPC](https://github.com/DCS-gRPC/rust-server)** | tactical replies, threat circle | Optional. Without it, everything else still works. |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) + Visual Studio 2022 | building from source only | Not needed to *use* the bot. |

## Project structure

```
Darkstar.csproj, *.cs        The bot: SRS connection, audio, wake word, replies
  └── Darkstar.Core/         Shared library: config, phrases, logging, DCS-gRPC, service management
  └── Darkstar.Gui/          Config editor (Blazor Hybrid over WPF)
  └── Darkstar.Tests/        The test suite - dotnet run --project Darkstar.Tests
installer/                   Inno Setup script for the distributable Setup.exe
docs/                        All documentation
.github/workflows/           Builds and tests every push
build-installer.ps1          Builds the Setup.exe (Release, dependencies, speech model, one command)
setup-dev-environment.ps1    Sets up a development machine
```

### Running the tests

```powershell
dotnet run --project Darkstar.Tests
```

Prints a readable transcript and exits 0 when everything held. It needs no SRS server, no DCS, no
Gemini key and no speech model — the assertions are about arithmetic, spoken sentences, and whether
the code and the documentation still agree with each other.

`Darkstar.csproj` sits in the repository root on purpose — its project references point at `Darkstar.Core\` relative to itself, so moving it into a subfolder breaks the solution.

## License

GPL-3.0 — see [LICENSE](LICENSE).
