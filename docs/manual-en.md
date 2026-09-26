# D.A.R.K.S.T.A.R. — Complete manual

*Deutsche Fassung: **[manual-de.md](manual-de.md)***

**D**igital **A**ssistant for **R**adio **K**eyword-activated **S**peech **T**ranscription **A**nd **R**esponse — a voice-controlled radio assistant for [DCS World](https://www.digitalcombatsimulator.com/) that joins a [DCS-SimpleRadio-Standalone](https://github.com/ciribob/DCS-SimpleRadio-Standalone) (SRS) server as an external AWACS-mode client.

Pilots key up on a monitored frequency, say the wake word, ask something over the radio — and the bot transcribes it, works out an answer, and transmits it back in a synthetic voice.

This manual covers everything: what you need, how to install it, every setting, how to use it in the air, and what to do when something doesn't work.

---

## Table of contents

1. [How it works](#1-how-it-works)
2. [What you need](#2-what-you-need)
3. [Installation](#3-installation)
4. [First start](#4-first-start)
5. [Configuration reference](#5-configuration-reference)
6. [The configuration editor (GUI)](#6-the-configuration-editor-gui)
7. [Using the bot on the radio](#7-using-the-bot-on-the-radio)
8. [Live mission data via DCS-gRPC](#8-live-mission-data-via-dcs-grpc)
9. [Running as a Windows Service](#9-running-as-a-windows-service)
10. [Files, logs and backups](#10-files-logs-and-backups)
11. [Troubleshooting](#11-troubleshooting)
12. [Wake word accuracy](#12-wake-word-accuracy)
13. [Costs, limits and privacy](#13-costs-limits-and-privacy)

---

## 1. How it works

One request travels through the bot like this:

```
Pilot transmits on 251.000
        │
        ▼
SRS server  ──UDP (Opus)──▶  Bot decodes audio
        │
        ▼
Vosk transcribes continuously, locally, offline
        │   wake word found ("Overlord")?
        ▼
Recording starts (including ~2 s of buffered audio from before the wake word)
        │   silence, or no more packets
        ▼
Recording ends
        │
        ├─▶ Tactical request ("bogey dope")?     → answer from live mission data (DCS-gRPC)
        ├─▶ Known phrase from phrases.json?      → fixed answer
        └─▶ otherwise                            → Google Gemini transcribes and answers
        │
        ▼
Reply text → DCS-SR-ExternalAudio.exe → Windows TTS → transmitted on the same frequency
```

Two things are worth knowing up front:

- **Wake-word detection runs entirely on your machine.** Vosk transcribes everything it hears locally and checks the text for the configured keyword. No audio leaves your PC for this step, there is no account and no per-request cost.
- **Only the actual question goes to the cloud**, and only when the wake word triggered — and only if the answer isn't already covered by a fixed phrase or by live mission data.

Each configured radio is fully independent: its own wake word, its own callsign, its own recording state. Two pilots can talk to two different frequencies at the same time without interfering with each other.

---

## 2. What you need

### Mandatory

| Requirement | Notes |
|---|---|
| **Windows** | The bot uses `DCS-SR-ExternalAudio.exe` and Windows TTS voices for its replies. |
| **.NET 8** | The **Desktop Runtime** covers both the bot and the GUI. Needed to *run* it. The installer brings it along; from source you need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). |
| **A running SRS server** | The bot connects as an external AWACS-mode client. It does not have to run on the same machine, but `DCS-SR-ExternalAudio.exe` currently transmits to `127.0.0.1` (see [Troubleshooting](#11-troubleshooting)). |
| **`DCS-SR-ExternalAudio.exe`** | Ships with DCS-SimpleRadio-Standalone, in its `ExternalAudio` subfolder. Used for every reply. |
| **A Vosk speech model** | Offline, free, no account. [Download list](https://alphacephei.com/vosk/models) — see the table below for which one to pick. |
| **A Google Gemini API key** | Free to create at [Google AI Studio](https://aistudio.google.com/apikey). Only needed for transcription and freely generated replies; the free tier is enough for testing and small groups. |
| **Visual C++ Redistributable (x64)** | Vosk's native `libvosk.dll` needs it. The installer and the dev-setup script both handle this. |

### Optional

| Optional | What it adds |
|---|---|
| **WebView2 Runtime** | Required by the GUI configuration editor (preinstalled on current Windows versions; the installer adds it if missing). |
| **DCS-gRPC server** | Enables the tactical replies ("bogey dope", "picture", "threat check") and the mission data explorer. See [chapter 8](#8-live-mission-data-via-dcs-grpc). |
| **A Discord webhook** | Start/stop and connection-loss notifications. |
| **Visual Studio 2022** | Only for building from source, with the workloads *.NET desktop development* and *ASP.NET and web development*. |

### Which Vosk model?

Recognition quality is the single biggest factor in how well the bot works. The small model is fast but weak; if the bot mishears wake words or triggers on the wrong ones, this is almost always the reason.

| Size | Model | Disk | Recommendation |
|---|---|---|---|
| Small | `vosk-model-small-en-us-0.15` | ~40 MB | Only for a first smoke test. Noticeably error-prone. |
| **Standard** | `vosk-model-en-us-0.22` | ~1.8 GB | **Recommended.** Clearly better wake-word accuracy, still fast enough. |
| Large | `vosk-model-en-us-0.42-gigaspeech` | ~2.3 GB | Best accuracy, higher CPU and RAM usage. |

The dev-setup script can download any of them for you (`-VoskModelSize Standard`).

---

## 3. Installation

### The normal way: the installer from the Releases page

1. Download the latest **`DARKSTAR-Setup-<version>.exe`** from the repository's **Releases** page.
2. Run it (it asks for administrator rights — it installs system runtimes).
3. Choose what to install:

   | Component | What it is |
   |---|---|
   | Bot service | The bot itself. Always installed. |
   | Config GUI | The graphical configuration editor. Recommended. |
   | Vosk model | The offline speech model. Only in the full installer, not in the `-slim` one. |

4. Optionally tick *"Install and start as a Windows Service"*. You can also do that later, and more comfortably, from the GUI — see [chapter 9](#9-running-as-a-windows-service).

The installer brings every runtime dependency with it and installs only what is actually missing: the **.NET 8 Desktop Runtime**, the **Visual C++ Redistributable** (needed by Vosk) and the **WebView2 Runtime** (needed by the GUI).

It also writes a `config.json` with the two paths it can work out for you, so neither has to be typed in afterwards:

- **`VoskModelPath`** — where it just put the speech model.
- **`ExternalAudioExePath`** — where **your** SRS installation is. It asks the registry where DCS-SimpleRadio-Standalone registered itself, and if that comes up empty, probes the usual install locations (both `Program Files` folders, per-user installs, and the root, `Program Files` and `Games` folder of every drive) for the known layouts — `ExternalAudio\DCS-SR-ExternalAudio.exe` first, then the older flat and `Server\` layouts.

An existing `config.json` is never overwritten, so a repair or an upgrade keeps your settings. If nothing is detected (SRS not installed yet, or installed somewhere unusual), the field simply keeps its default and can be filled in later with the **Detect SRS installation** button on **CH1 Connection**.

Two installer variants exist. The full one carries the speech model; the **`-slim`** one doesn't and is a few MB instead of up to ~2 GB — for machines that already have a model, in which case `VoskModelPath` has to be set by hand afterwards.

### Updating an existing installation

Run the new `Setup.exe` over the old one — there is nothing to uninstall first. It recognises the installed version and behaves accordingly:

| Situation | What happens |
|---|---|
| **Newer version** | Normal update. The install folder and your earlier component/task choices are reused, so it is Next, Next, Finish. |
| **Same version again** | Reinstall/repair. Same thing, just without a version change. |
| **Older version over a newer one** | It says so and asks whether you really want to. Answering no cancels without changing anything. |

What an update keeps and does for you:

- **`config.json`, `phrases.json` and `vocabulary.json` are never overwritten.** New settings added by the update are merged into your existing `config.json` by the bot at its next start, with their defaults, and a backup goes to `Backup\` first.
- **The Windows Service is stopped before any file is replaced and started again afterwards.** Without this the update would fail on a locked `Darkstar.exe`, because a running service holds its own executable open. If the service can't be restarted, the installer says so instead of leaving you guessing.
- **A service registration pointing at an old path is repaired** — the existing registration is removed and recreated.
- **The config editor is handled by Windows' Restart Manager**: if it's open, Setup offers to close it rather than failing on a locked file.

The "Ready to Install" page states which of the three situations it is before anything is written.

Versions come from the build: `.uild-installer.ps1 -Version 1.2` stamps `1.2` into the file name, the Setup.exe's file properties, and the uninstall entry that the *next* installer reads back. A build handed out without its own version number can't be told apart from the one before it, so bump it every time.


After installing, continue with [chapter 4](#4-first-start).

### Building the installer yourself

Only needed to produce a new `Setup.exe` (for a release, or with a different speech model). In PowerShell, from the repository root:

```powershell
.\build-installer.ps1                                        # full installer, small model
.\build-installer.ps1 -VoskModelSize Standard -Version 1.1   # recommended model, ~1.8 GB
.\build-installer.ps1 -Slim                                  # slim installer, no model
```

It checks the prerequisites, publishes both projects **in Release**, fetches the dependency installers and the model, and compiles everything into `installer\output\`. Details and all options: [building-the-installer.md](building-the-installer.md).

### Running from source

For working on the code rather than just using it. The setup script installs the .NET 8 SDK, the Visual Studio workloads, the runtimes and a speech model, then does a trial build:

```powershell
.\setup-dev-environment.ps1 -ProjectRoot "C:\path\to\repo" -VoskModelSize Standard
dotnet run --project Darkstar.csproj
```

Its parameters and the project layout are described in [contributing.md](contributing.md).

---

## 4. First start

1. **Open the Config Editor.** It's in the Start menu as *D.A.R.K.S.T.A.R. Config Editor*, or as `Darkstar.ConfigEditor.exe` in the installation folder. It finds the bot's `config.json` by itself.

   *(Running from source instead? Start the bot once — it writes a `config.json` next to the executable and exits, so you can review it before anything connects.)*

2. **Fill in the essentials:**

   | Setting | Channel in the GUI | Meaning |
   |---|---|---|
   | `SrsHost`, `SrsPort` | CH1 Connection | Your SRS server (default `127.0.0.1:5002`). |
   | `Coalition` | CH1 Connection | `2` = Blue, `1` = Red, `0` = Spectator. |
   | `ExternalAudioExePath` | CH1 Connection | Path to `DCS-SR-ExternalAudio.exe`, in the `ExternalAudio` folder of your SRS installation. Normally already filled in by the installer; **Detect SRS installation** finds it otherwise. |
   | `Radios` | CH2 Radios | The frequencies to monitor, each with wake word and callsign. |
   | `GeminiApiKey` | CH3 Speech | Your API key. |
   | `VoskModelPath` | CH3 Speech | Already filled in by the installer; only needed by hand after a `-slim` install or a source build. |

   Then **Save changes** — the previous version of each file is backed up automatically.

3. **Start the bot:** either register it as a Windows Service on **CH9 Service** (runs in the background and after every reboot), or simply run `Darkstar.exe` for a visible console window while testing.

4. **Check the log.** A healthy start shows, in order: phrases loaded, the wake word active with your radio list, "Connecting to SRS server …", "Connected. Waiting for hotword…".

5. **Check it in DCS:** the bot appears in the SRS client list under `ClientName` (default `DARKSTAR`). Key up on a monitored frequency and say the wake word plus a question.

To test the reply path on its own, without flying, send a transmission by hand:

```
cd "C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio"
DCS-SR-ExternalAudio.exe --text="Radio check, loud and clear." --freqs=251.000 --modulations=AM --coalition=2 --port=5002 --name="TEST"
```

If you hear that in DCS, TTS and SRS transmission both work, and anything still broken is on the recognition side.

---

## 5. Configuration reference

The bot reads three JSON files next to its executable. All three are created with sensible defaults if missing, validated on load (bad values produce a `WARNING` but never block startup), automatically extended with new fields after an update, and **always backed up to `Backup\` before any automatic change**.

> A full reference also lives in [configuration.md](configuration.md); this chapter is the same content in reading order.

### 5.1 `config.json`

#### Logging

| Field | Default | Description |
|---|---|---|
| `LoggingEnabled` | `true` | Master kill switch. `false` = nothing is logged at all. |
| `DebugLogging` | `false` | Verbose console output (UDP packets, raw JSON, volume readings). The log *file* always has full detail regardless. |

#### SRS connection

| Field | Default | Description |
|---|---|---|
| `SrsHost` | `"127.0.0.1"` | SRS server address. |
| `SrsPort` | `5002` | SRS server port. |
| `ClientName` | `"DARKSTAR"` | Name shown in the SRS client list. |
| `ExternalAwacsPassword` | `""` | Only if the server requires `EXTERNAL_AWACS_MODE` with a password. |

#### Radios

| Field | Default | Description |
|---|---|---|
| `Radios` | `[]` | The radios to monitor simultaneously. Each entry: `FrequencyHz`, `Modulation` (`"AM"`/`"FM"`), optional `Keyword`, optional `Callsign`. |
| `FrequencyHz` | `251000000` | Legacy single radio in Hz, only used while `Radios` is empty. |
| `Modulation` | `"AM"` | Legacy single radio modulation. |

```json
"Radios": [
  { "FrequencyHz": 251000000, "Modulation": "AM", "Keyword": "Overlord", "Callsign": "Overlord" },
  { "FrequencyHz": 127500000, "Modulation": "AM", "Keyword": "Texaco",   "Callsign": "Texaco" }
]
```

Each radio reacts **only** to its own wake word. Empty `Keyword`/`Callsign` fall back to the global `VoskKeyword`/`BotCallsign`.

#### Bot identity

| Field | Default | Description |
|---|---|---|
| `BotCallsign` | `"Overlord"` | Callsign the bot identifies with (global default, per radio overridable). |
| `PlayerNameCallsignSeparator` | `"\|"` | Cuts the pilot's callsign out of their SRS name: `Enfield 1-1 \| neodym` → the bot says "Enfield 1-1". `/`, `\`, `:`, `;`, `~` work too; `-` never does (it belongs to `1-1`). Squadron tags like `[ISAF]` are stripped and never spoken. |

Hyphens are replaced by spaces before speech, so "1-1" is spoken as "one one" and not "eleven".

#### Coalition security

| Field | Default | Description |
|---|---|---|
| `Coalition` | `2` | The bot's own side: `0` = Spectator, `1` = Red, `2` = Blue. |
| `RestrictToOwnCoalition` | `false` | `true` = requests from the opposing coalition are ignored entirely (no reply at all). Unknown/spectator senders are still answered. |

#### Wake word and recording

| Field | Default | Description |
|---|---|---|
| `VoskModelPath` | `""` | Folder of the unpacked Vosk model. Without it the bot falls back to a volume-based placeholder that recognizes no words at all. |
| `VoskKeyword` | `"computer"` | Global wake word, used by radios that don't set their own. |
| `HotwordAudioFilter` | `"LowPass"` | Audio preparation before Vosk. `"Average"` is the old path, for comparison only — see [chapter 12](#12-wake-word-accuracy). |
| `HotwordAutoGain` | `false` | Amplify quiet pilots for detection. Off by default; can cause false triggers. |
| `SaveRecordings` | `false` | Save every transmission to `recordings\` for measuring accuracy. |
| `SilenceFramesToStopRecording` | `50` | Silent 20 ms frames that end a recording (50 = 1 second). |
| `PreRollSeconds` | `2.0` | Seconds of audio kept before the wake word and prepended to the recording, so the start of the message isn't lost. |
| `HotwordEnergyThreshold` | `2000` | Only for the placeholder detector (no Vosk model). |
| `HotwordConsecutiveFramesNeeded` | `5` | Only for the placeholder detector. |

The keyword is matched as a **whole word**, case-insensitively.

#### Speech-to-text and replies (Google Gemini)

| Field | Default | Description |
|---|---|---|
| `GeminiApiKey` | `""` | API key. Without it there is no transcription. |
| `GeminiModel` | `"gemini-3.5-flash-lite"` | Model for the combined transcription + reply call. Flash-Lite variants have the highest free-tier quota. |
| `GeminiFallbackModel` | `""` | Optional second model, tried when the first keeps failing (e.g. quota exhausted). |
| `GeminiMaxRetries` | `2` | Retries per model on transient errors (429/500/502/503/504). |
| `GeminiRetryDelayMs` | `1000` | Delay between retries. |

Transcription and reply are one single API call — that halves the quota usage compared to separate calls.

#### Fixed phrases

| Field | Default | Description |
|---|---|---|
| `RestrictToKnownPhrases` | `true` | `true` = the bot answers **only** triggers from `phrases.json`, everything else gets `FallbackResponse`. `false` = anything unmatched is answered freely by Gemini. |
| `FallbackResponse` | `"Sorry, cannot answer that for you."` | The reply used when nothing matches. |

#### Reply audio

| Field | Default | Description |
|---|---|---|
| `ExternalAudioExePath` | `C:\Program Files\DCS-SimpleRadio-Standalone\ExternalAudio\DCS-SR-ExternalAudio.exe` | The SRS tool used for transmitting. Seeded by the installer from your actual SRS installation; **Detect SRS installation** on CH1 looks it up again at any time. |
| `VoiceName` | `""` | Windows TTS voice, e.g. `"Microsoft David Desktop"`. Empty = default voice. `DCS-SR-ExternalAudio.exe --help` lists the options. |
| `ExternalAudioExtraArgs` | `""` | Extra arguments appended to every transmission, for options this bot doesn't set itself. Whether your SRS build has a speaking-rate flag, and what it's called, depends on its version — check `--help`, then put it here (e.g. `--speed=-1`). |

#### "Standby" acknowledgement for slow replies

| Field | Default | Description |
|---|---|---|
| `AckEnabled` | `false` | Master switch for the holding message. |
| `AckAfterSeconds` | `4.0` | How long the bot may stay silent, counted **from the wake word**. If the real reply is ready sooner, nothing is sent. |
| `AckMessage` | `"{pilot}, this is {callsign}, message received, standby."` | `{pilot}` = the pilot's callsign, `{callsign}` = this radio's callsign. If the pilot is unknown, `"{pilot}, "` is dropped automatically. |

The bot never talks over a pilot: if the time expires while they are still transmitting, the acknowledgement goes out right after they stop. Acknowledgement and reply can never overlap.

#### DCS-gRPC connection

| Field | Default | Description |
|---|---|---|
| `DcsGrpcEnabled` | `false` | Master switch for everything that reads live mission data. |
| `DcsGrpcAddress` | `"http://127.0.0.1:50051"` | Address of the DCS-gRPC server, including the scheme. |
| `DcsGrpcApiKey` | `""` | DCS-gRPC 0.7.x has no authentication of its own — leave this empty unless you run it behind a proxy that requires a key. |

#### Tactical replies

| Field | Default | Description |
|---|---|---|
| `DcsIntelEnabled` | `false` | Master switch. Also needs `DcsGrpcEnabled`. |
| `DcsIntelContactSource` | `"AwacsThenMissionData"` | `AwacsThenMissionData`, `AwacsOnly` (god's eye off) or `MissionDataOnly`. See [chapter 8.3](#83-turning-on-the-tactical-replies). |
| `DcsIntelAwacsUnitName` | `""` | Mission-editor name of the unit whose sensors decide what may be reported. Empty = god's eye, unless the mode forbids it. |
| `DcsIntelMaxRangeNm` | `120` | Contacts further out are not reported. `0` = no limit. |
| `DcsIntelMaxGroups` | `3` | Groups reported per picture call before the rest is summarized. |
| `DcsIntelMagneticBearings` | `true` | Magnetic instead of true bearings (what the pilot reads on their instruments). |
| `DcsIntelIncludeHelicopters` | `true` | Include hostile helicopters. |
| `DcsIntelSayContactType` | `true` | Name the aircraft/helicopter type. DCS variant suffixes are stripped (`F-16C_50` → `F-16C`); a group flying several types is called "mixed, lead <type>". |
| `DcsIntelSlowSpeech` | `true` | Paces the numbers: comma between the digits of a bearing, ranges/altitudes/counts spelled out as words. Off = terser, faster phrasing. |
| `DcsIntelTimeoutSeconds` | `5` | Timeout per mission-data query. |
| `DcsIntelBogeyDopeTriggers` | `["bogey dope", "bogie dope", "bogey dobe", "boogie dope", "nearest bandit", "closest contact"]` | Matched case-insensitively anywhere in the transcription. The deliberate misspellings catch common recognition errors. |
| `DcsIntelPictureTriggers` | `["picture", "request picture", "say picture"]` | |
| `DcsIntelThreatTriggers` | `["threat check", "any threats", "threats"]` | |
| `DcsIntelBullseyeTriggers` | `["bullseye"]` | Forces the bullseye format instead of BRAA. |
| `DcsIntelNoContactsReply` | `"Picture clean."` | When nothing matches the filters. |
| `UnintelligibleReply` | `"Say again, your last was unreadable."` | Said when nothing intelligible was transcribed — instead of guessing at a request. |
| `DcsIntelUnavailableReply` | `"Negative, no tactical data available at this time."` | When the mission data can't be read at all. |
| `DcsAirfieldEnabled` | `false` | Answer "runway in use" / ATIS calls. Needs `evalEnabled = true` on the DCS-gRPC server for the runway part — see [8.4](#84-runway-in-use-and-atis). |
| `DcsAirfieldRunwayTriggers` | see [8.4](#84-runway-in-use-and-atis) | Phrases asking for the runway only. |
| `DcsAirfieldAtisTriggers` | see [8.4](#84-runway-in-use-and-atis) | Phrases asking for the full report. Checked first. |
| `DcsAirfieldPressureUnit` | `"Both"` | `Both` / `Hectopascals` / `InchesHg`. |
| `DcsAirfieldAtFieldNm` | `5` | Within this of an airfield, the pilot's position decides which one — no name needed. |
| `DcsAirfieldMaxDistanceNm` | `60` | Beyond this the bot asks which airfield. |

#### Threat circle (standing watch)

| Field | Default | Description |
|---|---|---|
| `DcsIntelThreatCircleEnabled` | `true` | Master switch (needs `DcsIntelEnabled`). |
| `DcsIntelThreatCircleDefaultRadiusNm` | `40` | Radius when the pilot names none. |
| `DcsIntelThreatCircleMaxRadiusNm` | `150` | Upper limit for a requested radius. |
| `DcsIntelThreatCirclePollSeconds` | `15` | Seconds between sweeps. Each sweep costs one set of mission queries per active circle. |
| `DcsIntelThreatCircleDurationMinutes` | `30` | Automatic expiry. |
| `DcsIntelThreatCircleMaxActive` | `8` | Circles running at the same time. |
| `DcsIntelThreatCircleMaxAlertsPerSweep` | `2` | Warnings per circle and sweep; the rest follow later. |
| `DcsIntelThreatCircleTriggers` | `["threat circle", "threat ring", "set threat circle"]` | Start phrases. |
| `DcsIntelThreatCircleCancelTriggers` | `["cancel threat circle", "stop threat circle", "threat circle off", "cancel threat ring"]` | Cancel phrases. |
| `DcsIntelThreatCircleNoPilotReply` | `"Unable to set the threat circle, cannot locate your aircraft."` | |
| `DcsIntelThreatCircleBusyReply` | `"Unable, too many threat circles active at the moment."` | |
| `DcsIntelThreatCircleCancelledReply` | `"Threat circle cancelled."` | |
| `DcsIntelThreatCircleNoneActiveReply` | `"No threat circle active for you."` | |


#### Discord notifications

| Field | Default | Description |
|---|---|---|
| `DiscordEnabled` | `false` | Master switch, off by default. |
| `DiscordWebhookUrl` | `""` | Webhook for start/stop and SRS connection loss/recovery. Create it in Discord under *Channel settings → Integrations → Webhooks*. |

### 5.2 `phrases.json`

Fixed trigger/answer pairs. If a trigger appears anywhere in the transcribed text (case-insensitive), its answer is used verbatim instead of a generated one. The first match wins.

```json
[
  { "Trigger": "radio check", "Response": "Radio check, loud and clear, five by five." },
  { "Trigger": "status",      "Response": "All systems nominal." },
  { "Trigger": "request rtb", "Response": "Copy, cleared to RTB." }
]
```

The transcription call still happens either way — the recognized text is what the trigger is matched against; only the *reply* is replaced.

### 5.3 `vocabulary.json`

A plain list of terms passed to Gemini as a hint. It doesn't change what the bot can talk about, it just improves recognition of words that aren't ordinary English:

```json
["Viggen", "Overlord", "Texaco", "Enfield", "Batumi"]
```

> **Never put a trigger phrase in here.** The transcriber is told to snap anything that merely *sounds like* a term in this list onto its exact spelling — that is what makes the hints work, and it does it to unintelligible audio too. A command phrase in this list therefore turns every mumble into that command, and since tactical requests are answered before phrases and Gemini, the bot confidently answers a request nobody made.
>
> `"Bogey Dope"` used to be one of the defaults, which is exactly how this was found: the bot replied with a bogey dope whenever it couldn't make out a transmission. It has been removed, and the bot now warns at startup — and the config editor marks the offending chips in red — if any vocabulary term is also a trigger phrase. Keep proper nouns here: callsigns, aircraft types, map names.

---

## 6. The configuration editor (GUI)

`Darkstar.ConfigEditor.exe` edits all three files graphically — and uses exactly the same code the bot itself uses to read and write them, so nothing can drift apart. It finds the bot's config folder automatically by searching its own folder, the folders above it, and their `bin\` subfolders.

The bot does **not** need to be stopped to look at settings, but changed settings only take effect when the bot is restarted.

| Channel | Contents |
|---|---|
| **CH1 Connection** | Config folder, SRS host/port, client name, EAM password, the `DCS-SR-ExternalAudio.exe` path with a **Detect SRS installation** button, coalition, coalition restriction. |
| **CH2 Radios** | The radio list: frequency, modulation, per-radio wake word and callsign, add/remove. |
| **CH3 Speech** | Gemini key/model/retries, TTS voice, pre-roll, Vosk model folder, global wake word, silence frames, the standby acknowledgement, and the placeholder detector's settings. |
| **CH4 Phrases** | The trigger/answer table plus `RestrictToKnownPhrases` and the fallback reply. |
| **CH5 Vocabulary** | The hint word list as chips. |
| **CH6 Discord** | Master switch and webhook URL. |
| **CH7 Logging** | Logging switches, resolved log folder, and a live tail of the newest log file. |
| **CH8 DCS-gRPC** | Connection test, tactical-reply settings with a test button, and the mission data explorer. |
| **CH9 Service** | Install, start, stop and cleanly remove the Windows Service. |

Bottom bar: **Discard (reload from disk)** and **Save changes**. Every save makes a timestamped backup first.

Details on CH8 and CH9 are in the next two chapters; a full walkthrough is in [gui.md](gui.md).

---

## 7. Using the bot on the radio

### The basic pattern

1. Select a monitored frequency in SRS.
2. Key up, say the **wake word** of that radio, then your request, in one transmission:
   *"Overlord, radio check."*
3. Release the PTT. The bot recognizes the end of the transmission from the silence (or from the packets stopping).
4. It answers on the same frequency: *"Enfield 1-1, this is Overlord… Radio check, loud and clear, five by five."*

The wake word may be anywhere in the sentence — the recording keeps ~2 seconds of what came before it, so nothing gets cut off if you trigger it mid-sentence.

### What determines the answer

The bot decides in this order:

1. **Tactical request** (bogey dope / picture / threat check) → answered from the running mission via DCS-gRPC. Takes priority because it's the only source that is actually *true*.
2. **Known phrase** from `phrases.json` → the fixed answer.
3. **Otherwise** → depends on `RestrictToKnownPhrases`: either the fallback reply, or a freely generated Gemini answer.

### Tactical calls

| Request | Example reply |
|---|---|
| *"Overlord, bogey dope"* | *"Bogey, bearing zero, niner, zero, thirty five miles, twenty two thousand, hot, group of two, type MiG-29."* |
| *"Overlord, picture"* | *"Picture: two groups. Lead group, bullseye two, seven, zero, for forty miles, twenty five thousand, two contacts, Su-27. …"* |
| *"Overlord, threat check"* | *"Nearest contact zero, niner, zero at thirty five miles, twenty two thousand."* |
| *"Overlord, bogey dope bullseye"* | Same as bogey dope, but positions given from the bullseye. |

Bearings are spoken digit by digit ("zero niner zero"), because TTS would otherwise read `090` as "ninety". With `DcsIntelSlowSpeech` (on by default) there is a comma between the digits and the other numbers are spelled out as words, which keeps the voice from rushing them. Aircraft and helicopter types are announced when `DcsIntelSayContactType` is on. Aspect follows standard brevity: **hot** (nose on), **flanking**, **beaming**, **cold** (running away).

For BRAA from your own aircraft, the bot has to find *your* aircraft: it matches your SRS name against the DCS player names. If that fails — for example because your SRS name is nothing like your DCS name — it automatically switches to the bullseye format instead of refusing the request.

### Airfield calls

| Request | Example reply |
|---|---|
| *"Overlord, Batumi, runway in use"* | *"Batumi, runway in use one three, wind one three zero at one niner knots."* |
| *"Overlord, Kobuleti ATIS"* | *"Kobuleti information, wind two one zero at eight knots, temperature one five, QNH one zero one three, altimeter two niner niner two, runway in use two five."* |
| *"Overlord, runway in use"* (no airfield named) | The airfield nearest your aircraft. |

The runway is the end with the most headwind, from live mission weather. Needs `evalEnabled = true` on the DCS-gRPC server for the runway part — see [chapter 8.4](#84-runway-in-use-and-atis). Taxiway instructions are not possible: DCS doesn't expose taxiways at all.

### Threat circle: a standing watch

Instead of asking again and again, a pilot can arm a watch around their own aircraft:

| On the radio | What happens |
|---|---|
| *"Overlord, threat circle forty miles"* | *"Threat circle active, forty miles."* — plus how many contacts are already inside. |
| *"Overlord, threat circle"* | Same, using the configured default radius. |
| *(a hostile enters the circle)* | *"Enfield 1-1, this is Overlord… Threat, bearing zero, niner, five, twenty two miles, twenty two thousand, hot, type MiG-29."* |
| *"Overlord, cancel threat circle"* | *"Threat circle cancelled."* |

The circle **moves with the aircraft** — it is always centred on where the pilot is now, not where they were when they asked. The radius can be spoken as digits or words ("forty", "twenty five", "one hundred").

Each contact is announced **once per circle**: a bandit that leaves and comes back does not trigger a second warning, so a contact loitering at the edge can't turn into a stream of calls. If several hostiles enter at once, the nearest ones are called first and the rest follow on the next sweeps rather than occupying the frequency all at once.

A circle ends when the pilot cancels it, after the configured time limit, or when the pilot leaves the mission (slot change, logout, shot down). Warnings wait for a pilot who is currently transmitting — but only briefly, since a late threat call is worse than a slightly overlapping one.


### Multiple radios

Every radio answers only to its own wake word and with its own callsign. A tanker frequency can run as "Texaco" while the AWACS frequency runs as "Overlord", both at the same time, each with its own conversation.

### Things that are normal

- **The bot doesn't hear itself.** While it transmits a reply, that radio is muted for itself — otherwise it would answer its own answer forever.
- **A reply takes a moment.** Transcription plus generation is typically 2–5 seconds. The standby acknowledgement (`AckEnabled`) exists to bridge exactly that gap.
- **It only reacts after the wake word.** Everything else on the frequency is ignored — but it *is* being transcribed locally the whole time, which is how the wake word is found.

---

## 8. Live mission data via DCS-gRPC

DCS-gRPC is a separate, free server component that exposes a running mission over the network. The bot uses it for the tactical replies and for the mission data explorer in the GUI.

### 8.1 Installing DCS-gRPC (on the DCS side)

Per the [official documentation](https://github.com/DCS-gRPC/rust-server):

1. Download the release archive and extract it into your DCS **Saved Games** folder (typically `C:\Users\<name>\Saved Games\DCS` or `…\DCS.openbeta_server`). Afterwards you have `Scripts\DCS-gRPC\`, `Mods\Tech\DCS-gRPC\` and `Scripts\Hooks\DCS-gRPC.lua` there.

2. Add this line to `MissionScripting.lua` in the DCS install folder (`…\DCS World\Scripts\MissionScripting.lua`), directly after `dofile('Scripts/ScriptingSystem.lua')`:

   ```lua
   dofile(lfs.writedir()..[[Scripts\DCS-gRPC\grpc-mission.lua]])
   ```

3. Create `Saved Games\DCS\Config\dcs-grpc.lua` so the server starts with every mission:

   ```lua
   autostart = true
   host = "127.0.0.1"   -- "0.0.0.0" if the bot runs on a different machine
   port = 50051
   ```

4. Start DCS and load a mission. To verify, look for `GRPC` entries in `Logs\dcs.log`, or for a `Logs\grpc.log` file.

> **Note:** DCS-gRPC 0.7.x has no authentication of its own. If the host listens on `0.0.0.0`, anyone who can reach that port can read mission data — restrict it with your firewall.

### 8.2 Connecting the bot

1. GUI → **CH8 DCS-gRPC** → switch **Enable DCS-gRPC** on, check address and port.
2. **Test connection.** A successful test also proves that a mission is actually running, because it asks for the mission's in-game time.

### 8.3 Turning on the tactical replies

Still on CH8, under **Tactical replies**:

- Switch **Answer tactical requests from mission data** on.
- **Contact source** and **AWACS unit name:** together they decide where contacts may come from — see below.
- Adjust range, group count and the trigger phrases as you like.
- **"Try it without flying"** shows the exact sentence the bot would speak, plus where the data came from — nothing is transmitted.

#### Where the contacts come from

Two settings decide this: **Contact source** (`DcsIntelContactSource`) and the **AWACS unit name**.

| Mode | AWACS sensors | God's eye | "Sensors see nothing" means |
|---|---|---|---|
| **AWACS sensors, mission data as fallback** (default) | used when a unit is named | used as fallback | fall back to mission data |
| **AWACS sensors only** | required | **never** | a clean picture is reported |
| **Mission data only** | ignored | always | — |

In the **default** mode the bot asks the unit's sensors first and uses plain mission data whenever they deliver nothing — on an error, a missing unit, *and* on an empty detection table. That last case matters: DCS only fills the detection table for **AI-controlled** units, so a player-flown AWACS always returns an empty list, and over the API "currently sees nothing" is indistinguishable from "can never see anything". Taking it at face value would make the bot call "picture clean" while ten bandits are inbound. The price of that safety is that while your AWACS detects nothing, you silently get god's eye.

**AWACS sensors only** turns that off: what the unit detects is what gets reported, and an empty table is answered honestly as "picture clean". This is the realistic setting — but only with an **AI** AWACS, since a player-flown one will always look blind. If the sensor call fails, or no unit is named, the bot says "no tactical data" instead of pretending the sky is empty, and a threat circle skips that sweep rather than implying the airspace is clear.

**Mission data only** ignores the unit name entirely and always reports every hostile aircraft.

Which source was actually used is in every `[Intel]` log line (`source=AWACS 'Overlord-1' sensors` or `source=mission data (god's eye)`), and *"Try it without flying"* shows the same — the quickest way to check you aren't silently running on the fallback.

### 8.4 Runway in use and ATIS

Still on CH8, under **Answer "runway in use" and ATIS calls**. Once it is on, a pilot can ask:

> *"Overlord, Batumi, runway in use."*
> *"Batumi, runway in use one three, wind one three zero at one niner knots."*

> *"Overlord, Kobuleti ATIS."*
> *"Kobuleti information, wind two one zero at eight knots, temperature one five, QNH one zero one three, altimeter two niner niner two, runway in use two five."*

#### You don't have to pronounce the airfield

Airfield names are the weakest part of this: "Mineralnye Vody", "Kobuleti", "Batumi" are exactly the words speech recognition gets wrong, and a mis-transcription that happens to match a *different* airfield gives a confidently wrong answer.

So the bot uses **where you are** first, and the name only as a tiebreaker. Sitting on the ramp at Batumi:

> *"Overlord, active runway for Punch 1-1."*
> *"Punch 1-1, this is Overlord… Batumi, runway in use one three, wind one three zero at one niner knots."*

No airfield spoken at all. Two things make that work:

- **Within `DcsAirfieldAtFieldNm` (5 NM by default) of an airfield's centre, your position decides** — parked, taxiing or in the circuit. A garbled word that looked like another airfield's name is ignored. Naming a *different* airfield deliberately still works: it is reported, and the log notes that you were somewhere else.
- **Saying your own callsign identifies you**, as a second route when your SRS name and your DCS name don't line up. "for Punch 1-1" and "for Punch one one" both work — spoken digits are turned back into figures before matching, and two pilots named in one transmission is refused rather than guessed at. This helps the tactical replies too: the same lookup decides whether a bogey dope can give BRAA from your aircraft or has to fall back to bullseye.

Airborne and more than `DcsAirfieldMaxDistanceNm` (60 NM) from anything, with no airfield named, the bot asks which one rather than reporting a field hundreds of miles away as if it were yours.

The reply always names the airfield it used, and the log line says how it was chosen (`the pilot is at it, 0.3 NM from the centre` / `named in the request` / `nearest to the pilot, 12 NM`) — so a wrong pick is audible and traceable rather than silent.

#### One setting on the DCS-gRPC server

Wind, temperature and pressure have proper DCS-gRPC calls. **Runway headings do not.** They exist only as `Airbase.getRunways()` inside DCS, which DCS-gRPC exposes exclusively through its `Eval` method — and `Eval` is **disabled by default**.

So in the DCS-gRPC server configuration:

```lua
evalEnabled = true
```

Then restart the mission. Without it the bot still reports the weather and says *"runway unknown"* — and writes one log line naming exactly this setting rather than failing silently.

Since `Eval` runs arbitrary Lua, it is worth knowing what the bot actually does with it:

- The Lua is a **constant in the source** (`DcsAirfieldService.RunwayQueryLua`). Nothing a pilot says, and nothing from any configuration field, is ever pasted into it — the snippet asks for *all* airfields at once precisely so that no airfield name has to be interpolated.
- It only **reads**. No unit is spawned, no flag is set, no message is sent.
- It runs **once per mission**. Runways don't move, so the result is cached until the DCS session changes.

A test asserts these properties on every build.

#### How the runway is chosen

Each runway strip can be used from either end. For every end the bot works out the wind component along it and picks the most headwind — which is what "runway in use" means. Exact ties (dead calm, or a pure crosswind) are broken by the smaller crosswind first, then the longer runway, so the answer doesn't depend on the order DCS happened to list the strips in.

The designator is the magnetic heading rounded to the nearest ten, with 0 becoming 36. Where DCS supplies its own name for the strip and it agrees to within one, that name is used instead — it is what the terrain's charts and your kneeboard show, including a `L`/`R` suffix.

Bearings follow `DcsIntelMagneticBearings` like everywhere else: the wind DCS reports is true, and is converted to magnetic for the report unless you turned that off.

#### Settings

| | |
|---|---|
| **Runway / ATIS triggers** | The phrases that ask for each. ATIS is checked first, so a call containing both gets the fuller answer. |
| **Altimeter setting** | `Both` reads QNH in hectopascals and then the inches setting — the practical choice for a mixed flight. Or pick one. |
| **Try it without flying** | Runs a real request and shows the sentence plus which airfield was used, the wind, and why that runway won. Nothing is transmitted. |

#### What is not possible

**Taxiways.** DCS does not expose them — not through gRPC, not through its own scripting API. They are part of the terrain model. Runways and parking spots are the limit of what any tool can read out of a mission, so taxi instructions would have to be written by hand per airfield.

### 8.5 The mission data explorer

Below the tactical settings, the explorer shows what a running mission exposes — as raw JSON, with 38 queries across mission, time, world, coalition, players, units, weather and live event streams. It is strictly read-only: only `Get`/`Stream` calls are made, nothing in the mission is changed, so it is safe to use on a live server with players on it.

- **Run snapshot** runs every query that needs no input in one go.
- Names found in results (units, groups, airbases) are offered as autocomplete in the name fields, so you can drill down from *Groups* → *Units of a group* → *Detected targets*.
- Results can be copied or saved to `grpc-dumps\` in the config folder.

This is the tool to use when you want to know what else could be built on top of the mission data.

---

## 9. Running as a Windows Service

As a service the bot runs in the background and after every reboot, with nobody logged in.

The comfortable way is GUI → **CH9 SERVICE**:

- The panel shows the live state, the start type and **which executable is actually registered** — with a warning when that differs from the configured path, which is how a stale installation gets spotted.
- **Install**: the executable path is pre-filled with `Darkstar.exe` next to the config folder. Optionally with delayed auto-start (so the network and the SRS server are up first) and optionally started right away.
- **Stop and remove**: asks for confirmation, stops the service, waits until it has really stopped, and only then deletes it. That order matters — deleting a running service only marks it for deletion and leaves a ghost entry behind until the next reboot.
- **Start / Stop** for the installed service.

Every operation that changes a service asks for administrator rights via UAC; the editor itself does not need to run elevated. Reading the status needs no rights at all.

Two things worth knowing:

- **The service runs the executable you registered**, and reads `config.json` from *that* folder. If you keep a second copy of the bot somewhere for testing, make sure you're editing the config the service actually uses — the panel shows the registered path for exactly this reason.
- **A service has no console window.** Use the log files under `logs\` to see what it's doing — or the live tail on CH7 Logging, which is the same data without leaving the editor.
- **The log is written as it happens**, forced to disk about once a second. If Explorer shows the current log as 0 bytes, that is Windows not updating the directory entry for an open file; the content is there. Read it with the CH7 tail, a tail tool (`Get-Content -Wait`), or any editor — not by trusting the size column.

Alternatively the installer can register the service during installation, or you can do it by hand with `sc.exe`.

---

## 10. Files, logs and backups

Everything lives next to the bot's executable:

| Path | Contents |
|---|---|
| `config.json` | All settings. |
| `phrases.json` | Fixed question/answer pairs. |
| `vocabulary.json` | Transcription hints. |
| `logs\` | Timestamped log files, always with full detail. One per start, named `darkstar_<date>_<time>.log`. |
| `Backup\` | Automatic timestamped copies made before any automatic change. |
| `grpc-dumps\` | JSON results saved from the mission data explorer. |

The log is the first place to look when something is off. A healthy startup looks roughly like this:

```
N fixed reply phrase(s) loaded from phrases.json.
Wake word detection active (Vosk, offline), 2 radio(s):
  251.000 MHz (AM): wake word "Overlord", callsign "Overlord"
  127.500 MHz (AM): wake word "Texaco", callsign "Texaco"
Connecting to SRS server 127.0.0.1:5002, monitoring: ...
Connected. Waiting for hotword...
```

And during a request:

```
[Hotword Detected] 251.000 MHz: starting recording (sender: "Enfield 1-1 | neodym" -> callsign "Enfield 1-1", ...)
[Recording finished] 251.000 MHz: 96000 bytes of PCM, transcribing...
[STT] "overlord bogey dope"
[Intel] 251.000 MHz: BogeyDope request answered from mission data (source=..., contacts=3, reference=unit 'Enfield-1-1')
[Reply] 251.000 MHz: "Enfield 1-1, this is Overlord... Bogey, bearing ..."
```

The `[STT]` lines are the most useful of all: they show what the bot actually *understood*, which answers most "why did it do that?" questions immediately.

---

## 11. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| **Bot stops right after starting, log says "the wake word model could not be loaded"** | The folder in `VoskModelPath` is missing or doesn't hold a Vosk model. The log names the folder and what to do; a model folder contains `am\`, `conf\`, `graph\` and `ivector\`. After a `-slim` install the model has to be downloaded separately. |
| **Bot doesn't appear in the SRS client list** | Wrong `SrsHost`/`SrsPort`, server not running, or firewall. Check the log for the connection line. |
| **Bot answers the wrong request when it didn't understand you** | A trigger phrase is sitting in `vocabulary.json` — see [5.3](#53-vocabularyjson). The bot warns about this at startup and the config editor marks it in red. The `[STT]` line in the log shows what was actually transcribed, and the `[Intel]` line below it names the trigger that fired. |
| **Bot reacts to nothing** | `VoskModelPath` empty or wrong (the log says so at startup), wrong frequency/modulation, or the wake word isn't being recognized — check the `[STT]` lines and [chapter 12](#12-wake-word-accuracy). |
| **Wake word is only recognized sometimes** | Model too weak. Switch to `Standard` (`-VoskModelSize Standard`) and delete the old model folder first. This is by far the most common cause. To measure it rather than guess: [chapter 12](#12-wake-word-accuracy). |
| **A radio reacts to the wrong wake word** | Almost always a mis-transcription by a weak model — check `[STT]`. Keywords are matched as whole words, so a longer word containing the keyword won't trigger it. |
| **No reply, log shows a Gemini error** | Key missing/invalid, or quota exhausted. Set `GeminiFallbackModel`, or wait for the quota to reset. |
| **Reply is generated but never heard** | `ExternalAudioExePath` wrong, or TTS voice not installed. Press **Detect SRS installation** on CH1, then test transmitting manually (see [chapter 4](#4-first-start)). The log names the detected path when the configured one doesn't exist. |
| **Bot answers its own replies** | Should be impossible — the radio is self-muted while transmitting. If it happens anyway, please report it with the log. |
| **Numbers are rattled off / hard to understand** | Switch `DcsIntelSlowSpeech` on (GUI: CH8 → *Slow, clearly spoken numbers*). If the voice itself is too fast overall, try a different `VoiceName`, or a speaking-rate flag via `ExternalAudioExtraArgs` if your SRS version supports one. |
| **Reply arrives very late** | Normal for 2–5 s. Turn on the standby acknowledgement so pilots know they were heard. |
| **Threat circle warnings never arrive** | Check the log for `[ThreatCircle]` lines: they show every sweep result. Most often the pilot's aircraft can't be matched to their SRS name (the circle needs it as its centre), or the circle already expired. |
| **Tactical requests say "no tactical data"** | DCS-gRPC not running, no mission loaded, wrong address, or `DcsGrpcEnabled` off. Test it on CH8. |
| **Tactical replies always use bullseye instead of BRAA** | Your SRS name couldn't be matched to a DCS player name. Make the two similar, or use the separator convention (`CALLSIGN 1-1 \| handle`). |
| **"Picture clean" although enemies are up** | The configured AWACS unit is player-flown or doesn't exist, plus a range limit that's too tight. Clear `DcsIntelAwacsUnitName` for a test, or raise `DcsIntelMaxRangeNm`. |
| **Service won't start** | Usually a path problem: check the registered executable on CH9, and that `config.json` sits in *that* folder. |
| **Service can't be removed** | Something still holds a handle on it (services.msc, Task Manager). Close both and retry; a reboot always clears it. |
| **GUI shows "config.json exists but has invalid JSON"** | Deliberate: the editor refuses to overwrite a broken file with defaults. Fix the syntax (the log names the position) and reload. |
| **Transmission to a remote SRS server doesn't arrive** | `DCS-SR-ExternalAudio.exe` is currently called without a server parameter, so it transmits to `127.0.0.1`. With a remote SRS server the replies stay local. Check `--help` for the right parameter name and ask for it to be wired in. |

Still stuck? The log file plus the `[STT]` lines around the failure usually explain it, and are what to include in a bug report (with `GeminiApiKey`, `DiscordWebhookUrl` and `DcsGrpcApiKey` redacted).

---

## 12. Wake word accuracy

A wake word that is missed, or that fires when nobody said it, is the most common complaint about a setup like this. Three things decide it, in this order.

### 12.1 The model size (biggest effect by far)

The small model (~40 MB) is a compromise for machines that have nothing to spare. It mis-transcribes readily, and every mis-transcription is a chance to either miss your keyword or invent it. Switching to `Standard` (~1.8 GB) usually settles the matter:

```powershell
.\setup-dev-environment.ps1 -VoskModelSize Standard    # from source
.\build-installer.ps1 -VoskModelSize Standard          # into a new installer
```

Delete the old model folder first, then point `VoskModelPath` at the new one.

### 12.2 Audio preparation (CH3 Speech → "Audio preparation")

SRS delivers 48 kHz audio; Vosk wants 16 kHz. Getting from one to the other means discarding two of every three samples — and anything above 8 kHz in the original doesn't simply disappear when you do that. It folds back down into the audible range as a mirror image: 10 kHz reappears at 6 kHz, 11 kHz at 5 kHz, 12 kHz at 4 kHz. That is precisely the band where consonants are told apart, which is why the effect shows up as words being misheard rather than as audible noise.

| Setting | What it does |
|---|---|
| **Low-pass** (default) | Filters the audio first, so there is nothing above 8 kHz left to fold down. Measured against test tones, fold-over products land 60–79 dB down. |
| **Average** (old) | The previous behaviour: average three samples, drop two. Measured the same way, fold-over products land only 5–22 dB down, i.e. most of it still gets through. |

`Average` is kept for one purpose: comparing the two on your own recordings (see 12.4). There is no reason to run it otherwise.

### 12.3 Quiet pilots (optional)

**"Even out quiet pilots"** amplifies transmissions that arrive too quiet for the recogniser. It is off by default on purpose: any automatic gain also lifts background noise, and noise lifted into speech range is exactly what produces wake words nobody said. If you turn it on, watch the false-trigger rate afterwards. It never touches the audio that gets transcribed or saved — only what the detector hears.

### 12.4 Measuring instead of guessing

Turn on **"Save every transmission to `recordings\`"** (CH3 Speech) and fly normally for a while. The bot writes one WAV per transmission, tagged with what happened:

- `..._hit_...` — the wake word fired.
- `..._missed_...` — someone transmitted and it did not fire. These are the interesting ones, and they are the reason this exists: a miss otherwise leaves no trace anywhere.

Then let the bot grade itself:

```powershell
Darkstar.exe --test-hotword recordings --compare
```

This runs every recording through the real detector, in the same 20 ms frames the live path uses, with both audio preparations side by side — and prints how many came out as expected. It connects to nothing, so it is safe to run while the service is live.

```
file                                   expected  Average   LowPass
------------------------------------------------------------------
..._0.000MHz_missed_Enfield 1-1.wav    trigger   MISSED    ok
..._0.000MHz_hit_Springfield 2-1.wav   trigger   ok        ok

Average: 4 of 6 as expected (67%), 2 missed, 0 fired when they shouldn't.
LowPass: 6 of 6 as expected (100%), 0 missed, 0 fired when they shouldn't.
```

Useful options: `--verbose` prints what Vosk actually transcribed, which is usually the moment it becomes obvious what went wrong; `--keyword` and `--model` try a different word or model without touching `config.json`; `--filter` runs just one of the two paths. `Darkstar.exe --test-hotword` with no path uses the `recordings` folder.

A rename is all it takes to add your own cases: a file with `_silence_` in its name is expected *not* to trigger, so you can keep a set of recordings that must never wake the bot — engine noise, other pilots' chatter, the bot's own voice.

> Recordings cost roughly 100 KB per second of speech and nothing ever deletes them. Turn the setting back off once you have measured what you needed.

### 12.5 If it is still wrong

| Symptom | Where to look |
|---|---|
| Fires on other words | A larger model. Keywords are matched as whole words already, so a longer word containing yours cannot trigger it — `--verbose` will show what was really heard. |
| Never fires for one particular pilot | Their level, not your settings: check with a recording, then consider 12.3. |
| Fires on the bot's own replies | Should be impossible — the radio is muted while transmitting. If it happens, keep the log and the recording. |
| Two radios react to each other's keyword | Almost always a mis-transcription by a weak model; `--verbose` confirms it. |

---

## 13. Costs, limits and privacy

- **What costs money:** nothing, in normal use. Vosk, SRS and DCS-gRPC are free and local; the Gemini free tier is enough for testing and small groups. Heavy use can exceed the free daily quota — that's what `GeminiFallbackModel` is for.
- **What leaves your machine:** only the recorded question, and only after the wake word triggered, and only when it isn't answered by a fixed phrase or by mission data. Continuous listening for the wake word happens entirely locally.
- **What is logged:** the recognized text of every processed request lands in the log file. If that matters in your group, say so — or turn logging off with `LoggingEnabled`.
- **Limits worth knowing:** replies use Windows TTS, so quality depends on the installed voices. Vosk works best on English speech. The tactical replies are only as good as the mission data DCS exposes.

---

## Further reading

- [README.md](../README.md) — project overview
- [configuration.md](configuration.md) — configuration reference
- [gui.md](gui.md) — the configuration editor in detail
- [contributing.md](contributing.md) — development notes
- [building-the-installer.md](building-the-installer.md) — building the installer
- [changelog.md](changelog.md) — what changed

License: GPL-3.0 — see [LICENSE](../LICENSE).
